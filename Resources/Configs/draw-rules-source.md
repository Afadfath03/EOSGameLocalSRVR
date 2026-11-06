# Draw rule provenance

Per the maintainer's corrected instruction on 2026-09-10, **client tables take precedence** over the initial
comparison table. This file records which client table supplies each rule and which local policies are AscNet
decisions rather than recovered retail behaviour.

## Rates and guarantees

`DrawProbShow.tsv` is read directly:

| Pool | Displayed rate |
| --- | --- |
| Normal characters | 0.5% |
| Fate | 1.5% |
| Weapons | 5% total (4% targeted plus two 0.5% off-targets) |
| Uniframes | 5% |
| CUBs | 5.82% |

`DrawServerRule.tsv` transcribes guarantees and fallback target rates from `DrawGroupRule.tsv`. Per-banner target
rates come from `DrawAimProbability.tsv`, so banners in one group may keep different rates. Arrival and targeted
weapons calibrate after an off-target rare. Target percentages apply conditional on obtaining the highest rarity, not
as additional independent rolls.

`DrawGroupRule.tsv` supplies group identities. `DrawPreview` supplies eligible rewards (both `GoodsId` and
`UpGoodsId`). Uniframes use the client's 100% target rule. CUB previews contain the selected S and lower-rarity CUBs,
so S targets are 100%. No calibration is invented for CUBs or Uniframes where the supplied rules leave it version
dependent.

**Member's banner:** initial limit is 40, then 60.

## Fate pools (local policy)

The client publishes Fate limits only as an inclusive 80–100 range: it specifies the range, the 1.5% base rate, and
equality with the corresponding normal pool's combined rate, but not the server's threshold weights.

By explicit maintainer request on 2026-10-06, local Fate groups **15** and **38** use a fixed guarantee at draw
**100**, without an invented ramp distribution. This keeps the displayed base rate at 1.5%; the combined rare rate
including the hard guarantee is computed by the server. This is a local AscNet rule, not a claim that the retail
server uses a fixed-100 threshold law.

## Crucible groups

Groups **35** and **36** retain their separate client-defined inheritance groups; they are not assumed to be
Phylotree Nexus. The server catalog currently provides no Phylotree identity and no Checked S selection/remaining-use
source, so those mechanisms require explicit catalog/selection data before activation.

## Persistence

Legacy lifetime counters remain intact for activity progress. A new persisted round stores misses, sampled limit, and
calibration independently. Migration preserves the legacy partial cycle; historical early-S resets cannot be fully
reconstructed from the old bounded history.
