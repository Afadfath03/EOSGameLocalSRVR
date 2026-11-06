#!/usr/bin/env python3
"""Append rows that are new in the 4.9 EN client tables to the server TSVs.

Source: alt3ri/PGR_Data EN/bytes JSON (the local binary decoder used for 4.8 is
not available on this machine). "New" means: the key exists in the 4.9 commit,
not in the 4.8 baseline commit, and not in the server TSV. Existing TSV rows are
never modified, and tables where the server deliberately keeps a subset of the
retail rows are left alone (their new retail rows were never selected).

The source JSON is lossy: integers may arrive as floats ("1.0"), omitted defaults
as null, and lists as JSON/Python-repr text ("[5]", "{1: 'a'}"). Each column's
encoding is learned from 4.8 rows that are also in the TSV. Those rows are then
re-encoded; a table is written only when its re-encoding reproduces the TSV
(so learned encodings are proven, and 4.8->4.9 value changes are not confused
with encoding errors). Tables that fail are reported, not guessed.

Usage: python Scripts/import_new_rows_4_9.py [--write] [--only share/x/Y ...]
"""
from __future__ import annotations

import argparse
import ast
import concurrent.futures
import hashlib
import json
import re
import sys
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TABLE = ROOT / "Resources/table"
MANIFEST_PATH = TABLE / "manifest.json"
SOURCE_REPO = "alt3ri/PGR_Data"
SOURCE_COMMIT = "e7c752ff9914b20272d5dc287e4d0b7c7223d0c6"  # "App Ver 4.9.0 | Document Ver 4.9.5"
BASELINE_COMMIT = "be22832ca7b40e3ea675fa205013ab495db0a047"  # "App Ver 4.8.0 | Document Ver 4.8.4"
CACHE_ROOT = ROOT / ".runtime/upgrade-4.9/PGR_Data"
SCRIPT = "Scripts/import_new_rows_4_9.py"
INDEXED = re.compile(r"^(.*?)\[(\d+)\]$")
INT = re.compile(r"^-?\d+$")
# Baseline rows are re-encoded and compared with the TSV. Scattered differences are
# value changes (the TSVs come from later 4.8 builds or local edits); a column that
# differs on most rows is an encoding error. Tables need both conditions.
MIN_AGREEMENT = 0.8
MAX_COLUMN_MISMATCH = 0.5
# A TSV holding fewer than this share of the baseline keys is a curated subset.
MIN_COVERAGE = 0.9


def fetch(url: str) -> bytes:
    with urllib.request.urlopen(url, timeout=60) as response:
        return response.read()


def source_index(commit: str = SOURCE_COMMIT) -> dict[str, str]:
    cached = CACHE_ROOT / commit / "tree.json"
    if not cached.exists():
        cached.parent.mkdir(parents=True, exist_ok=True)
        cached.write_bytes(fetch(f"https://api.github.com/repos/{SOURCE_REPO}/git/trees/{commit}?recursive=1"))
    tree = json.loads(cached.read_text(encoding="utf-8"))
    if tree.get("truncated"):
        raise SystemExit("source tree listing is truncated")
    prefix = "EN/bytes/"
    return {entry["path"][len(prefix):-5].lower(): entry["path"]
            for entry in tree["tree"]
            if entry["path"].startswith(prefix) and entry["path"].endswith(".json")}


def load_source(path: str, commit: str = SOURCE_COMMIT) -> tuple[list[dict], str]:
    local = CACHE_ROOT / commit / path
    if not local.exists():
        local.parent.mkdir(parents=True, exist_ok=True)
        local.write_bytes(fetch(f"https://raw.githubusercontent.com/{SOURCE_REPO}/{commit}/"
                                + urllib.parse.quote(path)))
    raw = local.read_bytes()
    rows = json.loads(raw.decode("utf-8-sig"))
    if isinstance(rows, dict):
        rows = list(rows.values())
    return [row for row in rows if isinstance(row, dict)], hashlib.sha256(raw).hexdigest()


def read_tsv(path: Path):
    raw = path.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    newline = "\r\n" if "\r\n" in text else "\n"
    lines = [line for line in text.replace("\r\n", "\n").split("\n")]
    while lines and lines[-1] == "":
        lines.pop()
    if not lines:
        return [], [], bom, newline
    header = lines[0].split("\t")
    rows = []
    for line in lines[1:]:
        cells = line.split("\t")
        if len(cells) < len(header):
            cells += [""] * (len(header) - len(cells))
        if len(cells) != len(header):
            raise ValueError(f"{path}: row has {len(cells)} cells, header has {len(header)}")
        rows.append(cells)
    return header, rows, bom, newline


def write_tsv(path: Path, header, rows, bom, newline) -> bytes:
    text = newline.join(["\t".join(header)] + ["\t".join(row) for row in rows]) + newline
    data = (b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8")
    path.write_bytes(data)
    return data


def column_map(header):
    columns, starts = [], {}
    for name in header:
        match = INDEXED.fullmatch(name)
        field, index = (match.group(1), int(match.group(2))) if match else (name, None)
        columns.append((field, index))
        if index is not None:
            starts[field] = min(starts.get(field, index), index)
    return columns, starts


def as_list(value, drop_zero_key: bool = False):
    """Source lists arrive as lists, JSON text, Python-repr text, or {index: value} maps.

    Maps holding key 0 are ambiguous: some server TSVs store key 0 as the first
    indexed column, others start at key 1 and omit key 0. Table learns which.
    """
    if value is None:
        return []
    if isinstance(value, str):
        text = value.strip()
        if text in ("", "[]", "{}"):
            return []
        if text[0] in "[{":
            try:
                value = json.loads(text)
            except json.JSONDecodeError:
                try:
                    value = ast.literal_eval(text)
                except (ValueError, SyntaxError):
                    return [value]  # text that merely starts with a bracket
            if not isinstance(value, (list, dict)):
                return [value]
        else:
            return [value]
    if isinstance(value, dict):
        indexed = {int(k): v for k, v in value.items()}
        base = 0 if 0 in indexed and not drop_zero_key else 1
        return [indexed.get(i) for i in range(base, max(indexed, default=base - 1) + 1)]
    if isinstance(value, (list, tuple)):
        return list(value)
    return [value]


def has_zero_key(value) -> bool:
    if isinstance(value, dict):
        return 0 in value or "0" in value
    return isinstance(value, str) and value.lstrip().startswith("{") and re.search(r"[{,]\s*0\s*:", value) is not None


def raw_value(row: dict, field: str, index, starts, drop_zero_key: bool = False):
    if field not in row:
        return False, None
    value = row[field]
    if index is None:
        if isinstance(value, (list, dict)) or (isinstance(value, str) and value[:1] in "[{" and len(value) > 1):
            values = as_list(value, drop_zero_key)
            if len(values) > 1:
                raise ValueError(f"multiple values for scalar column {field}")
            return True, values[0] if values else None
        return True, value
    values = as_list(value, drop_zero_key)
    offset = index - starts[field]
    return True, values[offset] if 0 <= offset < len(values) else None


def scalar_text(value, float_style: bool = False) -> str | None:
    """Text for a non-null scalar; integral floats collapse to ints unless the column is float-styled."""
    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, float):
        return repr(value) if float_style or not value.is_integer() else str(int(value))
    if float_style and isinstance(value, int):
        return repr(float(value))
    if isinstance(value, (list, dict)):
        raise ValueError(f"nested value {value!r}")
    text = str(value)
    if any(char in text for char in "\t\r\n"):
        raise ValueError(f"cell contains tab/newline: {text[:40]!r}")
    return text


class Table:
    def __init__(self, rel: str, header, rows, source_rows, key_fields, drop_zero_key: bool = False):
        self.rel, self.header, self.rows, self.source_rows = rel, header, rows, source_rows
        self.drop_zero_key = drop_zero_key
        self.columns, self.starts = column_map(header)
        self.key_idx = [header.index(field) for field in key_fields]
        self.float_style = [any(re.fullmatch(r"-?\d+\.\d+", cells[i]) for cells in rows)
                            for i in range(len(header))]
        self.null_text = self.learn_nulls()

    def identity_cells(self, cells):
        return tuple(cells[i] for i in self.key_idx)

    def learn_nulls(self):
        """Per column: the TSV text the source's null maps to, learned from overlap."""
        current = {}
        for cells in self.rows:
            current.setdefault(self.identity_cells(cells), cells)
        votes = [dict() for _ in self.header]
        for row in self.source_rows:
            cells = current.get(self.provisional_identity(row))
            if cells is None:
                continue
            for i, (field, index) in enumerate(self.columns):
                present, value = raw_value(row, field, index, self.starts, self.drop_zero_key)
                if present and value is None:
                    votes[i][cells[i]] = votes[i].get(cells[i], 0) + 1
        result = []
        for i, (field, index) in enumerate(self.columns):
            if votes[i]:
                result.append(max(votes[i].items(), key=lambda item: item[1])[0])
                continue
            # No evidence: keep the column's existing type. Numeric columns without
            # blanks keep their non-null default; anything else stays blank.
            existing = [cells[i] for cells in self.rows]
            numeric = existing and all(INT.match(c) or re.fullmatch(r"-?\d+\.\d+", c) for c in existing if c)
            result.append("0" if existing and numeric and all(existing) else "")
        return result

    def provisional_identity(self, row):
        out = []
        for i in self.key_idx:
            field, index = self.columns[i]
            present, value = raw_value(row, field, index, self.starts, self.drop_zero_key)
            out.append(self.null_text[i] if value is None and hasattr(self, "null_text")
                       else "" if value is None else scalar_text(value, self.float_style[i]))
        return tuple(out)

    def encode(self, row):
        cells = []
        for i, (field, index) in enumerate(self.columns):
            present, value = raw_value(row, field, index, self.starts, self.drop_zero_key)
            if not present:
                cells.append("")
            elif value is None:
                cells.append(self.null_text[i])
            else:
                cells.append(scalar_text(value, self.float_style[i]))
        return cells

    def overflow_fields(self, row):
        wide = {}
        for field, start in self.starts.items():
            width = sum(1 for f, i in self.columns if f == field)
            values = as_list(row.get(field), self.drop_zero_key)
            extra = [v for v in values[width:] if v not in (None, "", 0)]
            if extra:
                wide[field] = len(values)
        return wide


def split_skill_level_id(rows):
    """4.9 keys CharacterSkillLevelEffect by SkillLevelId = SkillId * 100 + Level; the
    server schema keeps SkillId and Level columns (CharacterPower, max skill level)."""
    for row in rows:
        if "SkillLevelId" in row and row["SkillLevelId"] is not None:
            skill_level_id = int(row["SkillLevelId"])
            row["SkillId"], row["Level"] = divmod(skill_level_id, 100)
    return rows


def sequential_ids(header, rows, appended, result=None):
    """The server's Id column here is a local row number (not in the source):
    AscNet policy numbers appended rows after the current maximum."""
    column = header.index("Id")
    next_id = max(int(cells[column]) for cells in rows) + 1
    for cells in appended:
        cells[column] = str(next_id)
        next_id += 1


def current_client_ids(header, rows, appended, result):
    """4.9 inserted rows mid-table, shifting the client row Id of later rows. The
    server keys this table by its natural key, so every row takes its 4.9 Id
    (matched by key) to keep Id unique and equal to the current client."""
    table, column = result["table"], header.index("Id")
    ids = {}
    for row in result["source_rows"]:
        ident = table.provisional_identity(row)
        ids[ident] = scalar_text(row["Id"])
    key_idx = [header.index(field) for field in result["key"]]
    for cells in rows + appended:
        ident = tuple(cells[i] for i in key_idx)
        if ident not in ids:
            raise ValueError(f"{result['rel']}: row {ident} absent from 4.9 source; cannot renumber Id")
        cells[column] = ids[ident]
    values = [cells[column] for cells in rows + appended]
    if len(set(values)) != len(values):
        raise ValueError(f"{result['rel']}: 4.9 Ids are not unique")


TRANSFORMS = {
    "share/character/skill/CharacterSkillLevelEffect": {
        "prepare": split_skill_level_id, "key": ["SkillId", "Level"], "finalize": sequential_ids},
    "share/equip/EquipBreakThrough": {"finalize": current_client_ids},
}


def inferred_types(header, rows):
    types = {}
    for i, name in enumerate(header):
        field = name.split("[")[0]
        kind, nullable = types.get(field, (None, False))
        for cells in rows:
            value = cells[i]
            if not value:
                nullable = True
            elif INT.match(value) and -2**31 <= int(value) < 2**31:
                kind = kind or "int"
            elif re.fullmatch(r"-?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?", value) and not (len(value) == 16 and re.fullmatch(r"[0-9A-Fa-f]{16}", value)):
                kind = "string" if kind == "string" else "double"
            else:
                kind = "string"
        types[field] = (kind, nullable)
    return types


def resolve_key(rel, header, rows, manifest_entry):
    if manifest_entry and manifest_entry.get("naturalKey"):
        fields = manifest_entry["naturalKey"].split(",")
        if all(f in header for f in fields):
            return fields
    for width in (1, 2, 3):
        fields = header[:width]
        if any(INDEXED.fullmatch(f) for f in fields):
            break
        idx = list(range(width))
        if len({tuple(r[i] for i in idx) for r in rows}) == len(rows):
            return fields
    return None


def evaluate(table, baseline_rows, current, header):
    """Re-encode baseline rows that are in the TSV; return (overlap, mismatched, per-column)."""
    overlap = mismatched = 0
    bad_columns = {}
    for row in baseline_rows:
        ident = table.provisional_identity(row)
        if ident not in current:
            continue
        overlap += 1
        encoded, old = table.encode(row), current[ident]
        diff = [header[i] for i in range(len(header))
                if encoded[i] != old[i] and table.columns[i][0] in row
                and not (raw_value(row, *table.columns[i], table.starts, table.drop_zero_key)[1] is None
                         and old[i] in ("", "0"))]
        if diff:
            mismatched += 1
            for column in diff:
                bad_columns[column] = bad_columns.get(column, 0) + 1
    return overlap, mismatched, bad_columns


def process(rel: str, source_path: str, baseline_path: str | None, manifest_by_path: dict):
    tsv = TABLE / f"{rel}.tsv"
    header, rows, bom, newline = read_tsv(tsv)
    if not header or not rows:
        return {"rel": rel, "skip": "empty TSV"}
    transform = TRANSFORMS.get(rel, {})
    entry = manifest_by_path.get(f"{rel}.tsv")
    key = transform.get("key") or resolve_key(rel, header, rows, entry)
    if not key:
        return {"rel": rel, "skip": "no unique key"}
    source_rows, source_sha = load_source(source_path)
    baseline_rows = load_source(baseline_path, BASELINE_COMMIT)[0] if baseline_path else []
    if "prepare" in transform:
        source_rows, baseline_rows = transform["prepare"](source_rows), transform["prepare"](baseline_rows)
    try:
        candidates = [False]
        if any(has_zero_key(value) for row in baseline_rows[:2000] for value in row.values()):
            candidates.append(True)
        best = None
        for drop_zero_key in candidates:
            # Learn encodings from the baseline (same build family as the TSV).
            table = Table(rel, header, rows, baseline_rows or source_rows, key, drop_zero_key)
            current = {table.identity_cells(c): c for c in rows}
            overlap, mismatched, bad_columns = evaluate(table, baseline_rows, current, header)
            if best is None or mismatched < best[2]:
                best = (table, overlap, mismatched, bad_columns, current)
        table, overlap, mismatched, bad_columns, current = best
        baseline_keys = {table.provisional_identity(row) for row in baseline_rows}
        new_rows, seen = [], set()
        for row in source_rows:
            ident = table.provisional_identity(row)
            if ident in seen:
                return {"rel": rel, "skip": f"duplicate source key {ident}"}
            seen.add(ident)
            if ident not in current and ident not in baseline_keys:
                new_rows.append(row)
        for row in new_rows:
            table.encode(row)  # surface encoding errors before deciding
    except (ValueError, SyntaxError, json.JSONDecodeError) as error:
        return {"rel": rel, "skip": f"encode error: {str(error)[:120]}"}
    if not new_rows:
        return {"rel": rel, "new": 0}
    source_fields = set().union(*(row.keys() for row in source_rows))
    missing = [name for name, (field, _) in zip(header, table.columns)
               if field not in source_fields and any(cells[header.index(name)] for cells in rows)
               and name not in key]
    coverage = overlap / len(baseline_keys) if baseline_keys else 1.0
    agreement = 1.0 if overlap == 0 else (overlap - mismatched) / overlap
    systematic = [(column, count) for column, count in bad_columns.items()
                  if overlap and count / overlap > MAX_COLUMN_MISMATCH]
    return {"rel": rel, "new": len(new_rows), "overlap": overlap, "baseline": len(baseline_keys),
            "coverage": coverage, "agreement": agreement, "systematic": systematic,
            "bad_columns": sorted(bad_columns.items(), key=lambda item: -item[1])[:6],
            "missing": missing, "drop_zero_key": table.drop_zero_key,
            "source": source_path, "source_sha": source_sha, "key": key, "transform": transform,
            "tsv": tsv, "header": header, "rows": rows, "bom": bom, "newline": newline,
            "table": table, "new_rows": new_rows, "source_rows": source_rows}


def widen(result):
    """Add indexed columns when a new row authors more list entries than the TSV holds."""
    table, header = result["table"], list(result["header"])
    need = {}
    for row in result["new_rows"]:
        for field, length in table.overflow_fields(row).items():
            need[field] = max(need.get(field, 0), length)
    if not need:
        return header, [list(r) for r in result["rows"]]
    rows = [list(r) for r in result["rows"]]
    for field, length in need.items():
        positions = [i for i, (f, idx) in enumerate(column_map(header)[0]) if f == field]
        start = column_map(header)[1][field]
        last = positions[-1]
        width = len(positions)
        added = [f"{field}[{start + n}]" for n in range(width, length)]
        header[last + 1:last + 1] = added
        for r in rows:
            r[last + 1:last + 1] = [""] * len(added)
    return header, rows


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--write", action="store_true", help="write TSVs and manifest (default: dry run)")
    parser.add_argument("--only", nargs="*", help="limit to table paths (relative, no .tsv)")
    args = parser.parse_args()

    manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    manifest_by_path = {t["path"]: t for t in manifest["tables"]}
    index = source_index()
    baseline_index = source_index(BASELINE_COMMIT)
    work = []
    for tsv in sorted(TABLE.rglob("*.tsv")):
        rel = tsv.relative_to(TABLE).with_suffix("").as_posix()
        if args.only and rel not in args.only:
            continue
        source = index.get(rel.lower())
        if source:
            work.append((rel, source, baseline_index.get(rel.lower())))

    with concurrent.futures.ThreadPoolExecutor(16) as pool:
        results = list(pool.map(lambda item: process(*item, manifest_by_path), work))

    applied, rejected, subsets, skipped, total = [], [], [], [], 0
    for result in results:
        if "skip" in result:
            skipped.append(result)
        elif not result["new"]:
            continue
        elif result["coverage"] < MIN_COVERAGE:
            subsets.append(result)
        elif result["agreement"] >= MIN_AGREEMENT and not result["systematic"]:
            applied.append(result)
        else:
            rejected.append(result)

    print(f"checked {len(results)} tables against {SOURCE_REPO}@{SOURCE_COMMIT[:12]}")
    for result in applied:
        total += result["new"]
        extra = []
        if result["agreement"] < 1.0:
            extra.append(f"value differences in {result['bad_columns'][:3]}")
        if result["missing"]:
            extra.append(f"columns absent from 4.9 source: {result['missing'][:6]}")
        if result["drop_zero_key"]:
            extra.append("index maps start at key 1")
        print(f"  + {result['new']:5} rows  {result['rel']}  (agreement {result['agreement']:.3f})"
              + ("".join(f"\n          {e}" for e in extra)))
    for result in rejected:
        print(f"  ! {result['new']:5} rows  {result['rel']}  REJECTED: overlap {result['overlap']}, "
              f"agreement {result['agreement']:.3f}, systematic {result['systematic']}, columns {result['bad_columns']}")
    for result in subsets:
        print(f"  = {result['new']:5} rows  {result['rel']}  curated subset ({result['overlap']}/{result['baseline']} baseline rows kept)")
    for result in skipped:
        print(f"  - skipped {result['rel']}: {result['skip']}")
    print(f"applying {total} rows in {len(applied)} tables; {len(rejected)} rejected; "
          f"{len(subsets)} curated subsets left alone; {len(skipped)} skipped")

    type_changes = []
    for result in applied:
        header, rows = widen(result)
        table = Table(result["rel"], header, rows, result["table"].source_rows, result["key"],
                      result["drop_zero_key"])
        appended = [table.encode(row) for row in result["new_rows"]]
        if "finalize" in result["transform"]:
            result["transform"]["finalize"](header, rows, appended, result)
        before = inferred_types(result["header"], result["rows"])
        after = inferred_types(header, rows + appended)
        for field, kind in after.items():
            if field in before and before[field] != kind:
                type_changes.append(f"{result['rel']}.{field}: {before[field]} -> {kind}")
        result["final"] = (header, rows + appended)
    if type_changes:
        print("generated-type changes:")
        for change in type_changes:
            print("  ~", change)

    if not args.write:
        print("dry run; pass --write to apply")
        return
    # Build every output (TSV bytes and manifest entries) before writing anything.
    outputs = []
    for result in applied:
        header, rows = result["final"]
        text = result["newline"].join(["\t".join(header)] + ["\t".join(row) for row in rows]) + result["newline"]
        data = (b"\xef\xbb\xbf" if result["bom"] else b"") + text.encode("utf-8")
        outputs.append((result["tsv"], data))
        path = f"{result['rel']}.tsv"
        note = (f"{SCRIPT}: appended {result['new']} rows new in 4.9 from "
                f"{SOURCE_REPO}@{SOURCE_COMMIT} {result['source']} (sha256:{result['source_sha']}), "
                f"keys absent from baseline @{BASELINE_COMMIT}; existing rows unchanged")
        if result["transform"].get("finalize") is sequential_ids:
            note += ("; SkillId/Level split from SkillLevelId (SkillId*100+Level) and Id numbered "
                     "after the current maximum (AscNet policy: local row number)")
        if result["drop_zero_key"]:
            note += "; index maps read from key 1 as in the existing rows"
        if result["transform"].get("finalize") is current_client_ids:
            note += ("; Id of every row set to its 4.9 client row Id by natural key "
                     "(4.9 inserted rows mid-table; server lookups use the natural key)")
        entry = manifest_by_path.get(path)
        if entry is None:
            entry = {"path": path, "naturalKey": ",".join(result["key"]), "filter": "existing rows retained",
                     "source": [], "transformation": []}
            manifest["tables"].append(entry)
            manifest_by_path[path] = entry
        entry["rows"] = len(rows)
        entry["columns"] = len(header)
        entry["contentSha256"] = hashlib.sha256(data).hexdigest()
        transformation = entry.get("transformation", [])
        if isinstance(transformation, str):
            transformation = [transformation]
        entry["transformation"] = transformation + [note]
    manifest_newline = "\r\n" if b"\r\n" in MANIFEST_PATH.read_bytes() else "\n"
    manifest_data = (json.dumps(manifest, indent=2, ensure_ascii=False) + "\n").replace("\n", manifest_newline)
    for path, data in outputs:
        path.write_bytes(data)
    MANIFEST_PATH.write_bytes(manifest_data.encode("utf-8"))
    print(f"written {len(outputs)} tables")


if __name__ == "__main__":
    sys.exit(main())
