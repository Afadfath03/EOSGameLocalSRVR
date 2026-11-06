//! Windows implementation of the OS boundary.

use anyhow::{ensure, Context, Result};
use std::{
    fs::{self, File, OpenOptions},
    os::windows::{
        ffi::OsStringExt,
        fs::{MetadataExt, OpenOptionsExt},
        io::AsRawHandle,
    },
    path::{Component, Path, PathBuf},
    process::{Child, Command},
};
use windows::{
    core::PWSTR,
    Win32::{
        Foundation::*,
        Security::{Authorization::*, *},
        Storage::FileSystem::*,
        System::{
            JobObjects::{
                AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation,
                SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
                JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
            SystemServices::{ACCESS_ALLOWED_ACE_TYPE, MAXIMUM_ALLOWED},
            Threading::*,
        },
    },
};

use crate::{GameRuntime, LaunchOptions, SteamInstall};

const DOCUMENT: &str = "PGR_Data/StreamingAssets/document";
const MODIFY: u32 = FILE_GENERIC_READ.0 | FILE_GENERIC_WRITE.0 | FILE_GENERIC_EXECUTE.0 | DELETE.0;

pub fn data_root() -> Result<PathBuf> {
    let base = std::env::var_os("LOCALAPPDATA").context("LOCALAPPDATA is not set")?;
    let base = PathBuf::from(base);
    ensure!(base.is_absolute(), "LOCALAPPDATA must be an absolute path");
    Ok(base)
}

pub fn launcher_install_dir() -> Result<PathBuf> {
    let exe = std::env::current_exe()?;
    let root = exe
        .parent()
        .context("launcher has no directory")?
        .to_path_buf();
    Ok(root)
}

pub fn launcher_asset_name() -> &'static str {
    "AscNetLauncher.exe"
}

pub fn executable_suffix() -> &'static str {
    ".exe"
}

/// Platform artifact name inside a release: `AscNetLauncher.zip` for Windows.
pub fn release_asset_name() -> &'static str {
    "AscNetLauncher.zip"
}

// ───────────────────────── document access (ACL grant) ─────────────────────────

struct Handle(HANDLE);
impl Drop for Handle {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}
struct LocalAllocation(*mut std::ffi::c_void);
impl Drop for LocalAllocation {
    fn drop(&mut self) {
        unsafe {
            let _ = LocalFree(HLOCAL(self.0));
        }
    }
}
fn handle(file: &File) -> HANDLE {
    HANDLE(file.as_raw_handle() as isize)
}

fn creation_time(process: HANDLE) -> Result<u64> {
    let (mut created, mut exited, mut kernel, mut user) = (
        FILETIME::default(),
        FILETIME::default(),
        FILETIME::default(),
        FILETIME::default(),
    );
    unsafe {
        GetProcessTimes(process, &mut created, &mut exited, &mut kernel, &mut user)?;
    }
    Ok((u64::from(created.dwHighDateTime) << 32) | u64::from(created.dwLowDateTime))
}

/// An authenticated process reference, not a user-selected SID.
pub fn caller_identity() -> Result<(u32, u64)> {
    unsafe { Ok((GetCurrentProcessId(), creation_time(GetCurrentProcess())?)) }
}

fn process_token(process: HANDLE) -> Result<Handle> {
    let mut token = HANDLE::default();
    unsafe {
        OpenProcessToken(process, TOKEN_QUERY | TOKEN_DUPLICATE, &mut token)?;
    }
    Ok(Handle(token))
}

fn token_user(token: HANDLE) -> Result<Vec<usize>> {
    let mut size = 0;
    unsafe {
        let _ = GetTokenInformation(token, TokenUser, None, 0, &mut size);
    }
    ensure!(
        size >= std::mem::size_of::<TOKEN_USER>() as u32,
        "caller token has no user SID"
    );
    let mut user = vec![0usize; (size as usize).div_ceil(std::mem::size_of::<usize>())];
    unsafe {
        GetTokenInformation(token, TokenUser, Some(user.as_mut_ptr().cast()), size, &mut size)?;
    }
    Ok(user)
}

fn file_information(file: &File) -> Result<BY_HANDLE_FILE_INFORMATION> {
    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    unsafe {
        GetFileInformationByHandle(handle(file), &mut info)?;
    }
    Ok(info)
}

fn open_pinned(path: &Path, access: u32) -> Result<File> {
    let file = OpenOptions::new()
        .access_mode(access)
        .share_mode(FILE_SHARE_READ.0)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS.0 | FILE_FLAG_OPEN_REPARSE_POINT.0)
        .open(path)
        .with_context(|| format!("opening document access path {}", path.display()))?;
    let info = file_information(&file)?;
    ensure!(
        info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT.0 == 0,
        "refusing document access through reparse point: {}",
        path.display()
    );
    ensure!(
        info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY.0 != 0 || info.nNumberOfLinks == 1,
        "refusing multiply-linked document file: {}",
        path.display()
    );
    Ok(file)
}

fn document_path(client: &Path) -> Result<PathBuf> {
    let client = std::path::absolute(client)?;
    ensure!(
        !client
            .components()
            .any(|c| matches!(c, Component::ParentDir)),
        "unsafe game path"
    );
    Ok(client.join(DOCUMENT))
}

fn pin_ancestors(path: &Path) -> Result<Vec<File>> {
    let mut ancestors: Vec<_> = path
        .parent()
        .context("document path has no parent")?
        .ancestors()
        .collect();
    ancestors.reverse();
    let mut pins = Vec::with_capacity(ancestors.len());
    for ancestor in ancestors {
        let file = open_pinned(ancestor, FILE_GENERIC_READ.0)?;
        ensure!(file.metadata()?.is_dir(), "document ancestor is not a directory");
        pins.push(file);
    }
    Ok(pins)
}

fn append_children(file: &File, path: &Path, paths: &mut Vec<PathBuf>) -> Result<()> {
    let mut buffer = [0u64; 8192];
    let bytes = std::mem::size_of_val(&buffer);
    loop {
        match unsafe {
            GetFileInformationByHandleEx(
                handle(file),
                FileIdBothDirectoryInfo,
                buffer.as_mut_ptr().cast(),
                bytes as u32,
            )
        } {
            Ok(()) => (),
            Err(error)
                if error.code() == windows::core::HRESULT::from_win32(ERROR_NO_MORE_FILES.0) =>
            {
                return Ok(())
            }
            Err(error) => {
                return Err(error)
                    .with_context(|| format!("enumerating pinned directory {}", path.display()))
            }
        }
        let mut offset = 0;
        loop {
            ensure!(
                offset + std::mem::size_of::<FILE_ID_BOTH_DIR_INFO>() <= bytes,
                "invalid directory record size"
            );
            let entry =
                unsafe { &*(buffer.as_ptr().cast::<u8>().add(offset).cast::<FILE_ID_BOTH_DIR_INFO>()) };
            let name_offset = offset + std::mem::offset_of!(FILE_ID_BOTH_DIR_INFO, FileName);
            ensure!(
                entry.FileNameLength % 2 == 0
                    && name_offset + entry.FileNameLength as usize <= bytes,
                "invalid directory record name"
            );
            let name = unsafe {
                std::slice::from_raw_parts(
                    buffer
                        .as_ptr()
                        .cast::<u8>()
                        .add(name_offset)
                        .cast::<u16>(),
                    entry.FileNameLength as usize / 2,
                )
            };
            let name = std::ffi::OsString::from_wide(name);
            if name != "." && name != ".." {
                ensure!(
                    Path::new(&name).components().count() == 1
                        && matches!(Path::new(&name).components().next(), Some(Component::Normal(_))),
                    "unsafe directory record name"
                );
                paths.push(path.join(name));
            }
            if entry.NextEntryOffset == 0 {
                break;
            }
            ensure!(
                entry.NextEntryOffset as usize >= std::mem::size_of::<FILE_ID_BOTH_DIR_INFO>()
                    && entry.NextEntryOffset % 8 == 0,
                "invalid directory record offset"
            );
            offset += entry.NextEntryOffset as usize;
        }
    }
}

fn pin_tree(root: &Path, access: u32) -> Result<Vec<File>> {
    let mut paths = vec![root.to_owned()];
    let mut pins = Vec::new();
    while let Some(path) = paths.pop() {
        let file = open_pinned(&path, access)?;
        let metadata = file.metadata()?;
        ensure!(path != root || metadata.is_dir(), "game document path is not a directory");
        if metadata.is_dir() {
            append_children(&file, &path, &mut paths)?;
        } else {
            ensure!(
                metadata.file_attributes() & FILE_ATTRIBUTE_READONLY.0 == 0,
                "document file is read-only: {}",
                path.display()
            );
        }
        pins.push(file);
    }
    Ok(pins)
}

/// Checks actual opens under the current caller's token, including existing files.
pub fn document_writable(client: &Path) -> Result<bool> {
    let result = (|| -> Result<bool> {
        let path = document_path(client)?;
        let _ancestors = pin_ancestors(&path)?;
        let tree = pin_tree(&path, MODIFY)?;
        let token = process_token(unsafe { GetCurrentProcess() })?;
        let user = token_user(token.0)?;
        let sid = unsafe { (*(user.as_ptr() as *const TOKEN_USER)).User.Sid };
        for file in &tree {
            if file.metadata()?.is_dir() {
                let (_allocation, _, dacl) = security(file)?;
                if !has_modify(dacl, sid, SUB_CONTAINERS_AND_OBJECTS_INHERIT)? {
                    return Ok(false);
                }
            }
        }
        Ok(true)
    })();
    match result {
        Ok(writable) => Ok(writable),
        Err(error)
            if error.chain().any(|cause| {
                cause
                    .downcast_ref::<std::io::Error>()
                    .is_some_and(|e| {
                        matches!(
                            e.kind(),
                            std::io::ErrorKind::PermissionDenied | std::io::ErrorKind::NotFound
                        )
                    })
            }) =>
        {
            Ok(false)
        }
        Err(error) => Err(error),
    }
}

fn authenticated_caller(pid: u32, created: u64) -> Result<(Handle, Handle)> {
    ensure!(
        pid != unsafe { GetCurrentProcessId() },
        "worker cannot name itself as caller"
    );
    let process = Handle(unsafe {
        OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
            false,
            pid,
        )?
    });
    ensure!(
        creation_time(process.0)? == created,
        "launcher caller process was replaced"
    );
    ensure!(
        unsafe { WaitForSingleObject(process.0, 0) } == WAIT_TIMEOUT,
        "launcher caller has exited"
    );
    let mut name = vec![0u16; 32768];
    let mut length = name.len() as u32;
    unsafe {
        QueryFullProcessImageNameW(
            process.0,
            PROCESS_NAME_WIN32,
            PWSTR(name.as_mut_ptr()),
            &mut length,
        )?;
    }
    let caller_image = PathBuf::from(std::ffi::OsString::from_wide(&name[..length as usize]));
    let caller_file = open_pinned(&caller_image, FILE_GENERIC_READ.0)?;
    let worker_file = open_pinned(&std::env::current_exe()?, FILE_GENERIC_READ.0)?;
    let a = file_information(&caller_file)?;
    let b = file_information(&worker_file)?;
    ensure!(
        (a.dwVolumeSerialNumber, a.nFileIndexHigh, a.nFileIndexLow)
            == (b.dwVolumeSerialNumber, b.nFileIndexHigh, b.nFileIndexLow),
        "permission caller is not this launcher executable"
    );
    let token = process_token(process.0)?;
    Ok((process, token))
}

fn security(file: &File) -> Result<(LocalAllocation, PSECURITY_DESCRIPTOR, *mut ACL)> {
    let mut descriptor = PSECURITY_DESCRIPTOR::default();
    let mut dacl = std::ptr::null_mut();
    unsafe {
        GetSecurityInfo(
            handle(file),
            SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION | OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION,
            None,
            None,
            Some(&mut dacl),
            None,
            Some(&mut descriptor),
        )
        .ok()?;
    }
    Ok((LocalAllocation(descriptor.0), descriptor, dacl))
}

fn has_modify(dacl: *mut ACL, sid: PSID, inheritance: ACE_FLAGS) -> Result<bool> {
    if dacl.is_null() {
        return Ok(true);
    }
    unsafe {
        for index in 0..u32::from((*dacl).AceCount) {
            let mut raw = std::ptr::null_mut();
            GetAce(dacl, index, &mut raw)?;
            let header = &*(raw as *const ACE_HEADER);
            if u32::from(header.AceType) != ACCESS_ALLOWED_ACE_TYPE {
                continue;
            }
            let ace = &*(raw as *const ACCESS_ALLOWED_ACE);
            if u32::from(ace.Header.AceFlags)
                & (INHERIT_ONLY_ACE.0 | NO_PROPAGATE_INHERIT_ACE.0)
                == 0
                && u32::from(ace.Header.AceFlags) & inheritance.0 == inheritance.0
                && ace.Mask & MODIFY == MODIFY
                && EqualSid(PSID(std::ptr::addr_of!(ace.SidStart) as *mut _), sid).is_ok()
            {
                return Ok(true);
            }
        }
    }
    Ok(false)
}

fn grant_modify(file: &File, sid: PSID) -> Result<()> {
    let (_allocation, _descriptor, dacl) = security(file)?;
    if dacl.is_null() {
        return Ok(());
    }
    let inheritance = if file.metadata()?.is_dir() {
        SUB_CONTAINERS_AND_OBJECTS_INHERIT
    } else {
        NO_INHERITANCE
    };
    if has_modify(dacl, sid, inheritance)? {
        return Ok(());
    }
    unsafe {
        let entry = EXPLICIT_ACCESS_W {
            grfAccessPermissions: MODIFY,
            grfAccessMode: GRANT_ACCESS,
            grfInheritance: inheritance,
            Trustee: TRUSTEE_W {
                TrusteeForm: TRUSTEE_IS_SID,
                TrusteeType: TRUSTEE_IS_USER,
                ptstrName: PWSTR(sid.0 as *mut u16),
                ..Default::default()
            },
        };
        let mut updated = std::ptr::null_mut();
        SetEntriesInAclW(Some(&[entry]), Some(dacl), &mut updated).ok()?;
        let _updated = LocalAllocation(updated.cast());
        SetSecurityInfo(
            handle(file),
            SE_FILE_OBJECT,
            DACL_SECURITY_INFORMATION,
            PSID::default(),
            PSID::default(),
            Some(updated),
            None,
        )
        .ok()?;
    }
    Ok(())
}

fn check_modify(file: &File, token: HANDLE) -> Result<()> {
    let (_allocation, descriptor, _) = security(file)?;
    let mapping = GENERIC_MAPPING {
        GenericRead: FILE_GENERIC_READ.0,
        GenericWrite: FILE_GENERIC_WRITE.0,
        GenericExecute: FILE_GENERIC_EXECUTE.0,
        GenericAll: FILE_ALL_ACCESS.0,
    };
    let mut privileges = [0u64; 128];
    let mut size = (privileges.len() * 8) as u32;
    let mut granted = 0;
    let mut allowed = BOOL::default();
    unsafe {
        AccessCheck(
            descriptor,
            token,
            MODIFY,
            &mapping,
            Some(privileges.as_mut_ptr().cast()),
            &mut size,
            &mut granted,
            &mut allowed,
        )?;
    }
    ensure!(
        allowed.as_bool(),
        "document ACL still denies the launcher user Modify access; existing deny entries were preserved"
    );
    Ok(())
}

/// Called only by the consented worker. The grant persists across Restore because
/// this is user data, not an owned patch file.
pub fn prepare_document_for_caller(client: &Path, pid: u32, created: u64) -> Result<()> {
    let (_caller, token) = authenticated_caller(pid, created)?;
    let user = token_user(token.0)?;
    let sid = unsafe { (*(user.as_ptr() as *const TOKEN_USER)).User.Sid };
    let mut impersonation = HANDLE::default();
    unsafe {
        DuplicateToken(token.0, SecurityImpersonation, &mut impersonation)?;
    }
    let impersonation = Handle(impersonation);
    let path = document_path(client)?;
    let _ancestors = pin_ancestors(&path)?;
    match fs::create_dir(&path) {
        Ok(()) => (),
        Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => (),
        Err(error) => return Err(error).context("creating persistent game document directory"),
    }
    let tree = pin_tree(&path, MAXIMUM_ALLOWED)?;
    for file in &tree {
        grant_modify(file, sid)?;
    }
    for file in &tree {
        check_modify(file, impersonation.0)?;
    }
    Ok(())
}

// ───────────────────────── process discovery and control ─────────────────────────

pub fn running_under() -> Result<bool> {
    use windows::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
        TH32CS_SNAPPROCESS,
    };
    struct Snapshot(HANDLE);
    impl Drop for Snapshot {
        fn drop(&mut self) {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }
    let snapshot = Snapshot(
        unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) }
            .context("querying running processes")?,
    );
    let mut entry = PROCESSENTRY32W {
        dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
        ..Default::default()
    };
    let mut next = unsafe { Process32FirstW(snapshot.0, &mut entry) };
    loop {
        match next {
            Ok(()) => {
                let length = entry
                    .szExeFile
                    .iter()
                    .position(|&c| c == 0)
                    .unwrap_or(entry.szExeFile.len());
                if String::from_utf16_lossy(&entry.szExeFile[..length])
                    .eq_ignore_ascii_case("PGR.exe")
                {
                    return Ok(true);
                }
            }
            Err(e) if e.code() == ERROR_NO_MORE_FILES.to_hresult() => return Ok(false),
            Err(e) => {
                return Err(e)
                    .context("listing running processes; refusing to assume the game is stopped")
            }
        }
        next = unsafe { Process32NextW(snapshot.0, &mut entry) };
    }
}

/// Keeps every child in a kill-on-close job so stopping the launcher stops the backend.
pub struct ProcessGuard(JobHandle);

struct JobHandle(HANDLE);
impl Drop for JobHandle {
    fn drop(&mut self) {
        if !self.0.is_invalid() {
            unsafe {
                let _ = CloseHandle(self.0);
            }
        }
    }
}

impl ProcessGuard {
    pub fn new() -> Result<Self> {
        unsafe {
            let job = JobHandle(CreateJobObjectW(None, None)?);
            let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            SetInformationJobObject(
                job.0,
                JobObjectExtendedLimitInformation,
                &info as *const _ as _,
                std::mem::size_of_val(&info) as u32,
            )?;
            Ok(Self(job))
        }
    }

    pub fn assign(&self, child: &Child) -> Result<()> {
        unsafe {
            AssignProcessToJobObject(self.0 .0, HANDLE(child.as_raw_handle() as isize))?;
        }
        Ok(())
    }
}

pub fn hide_console(command: &mut Command) -> &mut Command {
    use std::os::windows::process::CommandExt;
    command.creation_flags(windows::Win32::System::Threading::CREATE_NO_WINDOW.0);
    command
}

/// Process groups are a Unix concept; Windows uses the job object in `ProcessGuard`.
pub fn isolate_process_group(_command: &mut Command) {}

pub fn terminate_tree(child: &mut Child) {
    let _ = child.kill();
    let _ = child.wait();
}

pub fn free_space(path: &Path) -> Option<u64> {
    use std::os::windows::ffi::OsStrExt;
    use windows::{core::PCWSTR, Win32::Storage::FileSystem::GetDiskFreeSpaceExW};
    let wide: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
    let mut available = 0u64;
    unsafe {
        GetDiskFreeSpaceExW(PCWSTR(wide.as_ptr()), Some(&mut available), None, None).ok()?;
    }
    Some(available)
}

/// Holds an exclusive lock on `path` for the lifetime of the returned file.
pub fn exclusive_lock(path: &Path) -> Result<std::fs::File> {
    use std::fs::OpenOptions;
    use std::os::windows::fs::OpenOptionsExt;
    OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .share_mode(0)
        .open(path)
        .context("another local setup is already active")
}

pub fn is_reparse(path: &Path) -> Result<bool> {
    let metadata = fs::symlink_metadata(path)?;
    Ok(metadata_is_link(&metadata))
}

/// Link/reparse test that works from metadata already obtained for the same path.
pub fn metadata_is_link(metadata: &fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    metadata.file_type().is_symlink() || metadata.file_attributes() & 0x400 != 0
}

pub fn atomic_replace(source: &Path, destination: &Path) -> Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows::{
        core::PCWSTR,
        Win32::Storage::FileSystem::{
            MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH,
        },
    };
    let source: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
    let destination: Vec<u16> = destination
        .as_os_str()
        .encode_wide()
        .chain(Some(0))
        .collect();
    unsafe {
        MoveFileExW(
            PCWSTR(source.as_ptr()),
            PCWSTR(destination.as_ptr()),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
        .context("atomically replacing file")
    }
}

// ───────────────────────── game launch ─────────────────────────

pub fn launch_game(client: &Path, options: &LaunchOptions, _runtime: &GameRuntime) -> Result<()> {
    let executable = client.join("PGR.exe");
    let mut command = Command::new(&executable);
    command
        .current_dir(client)
        .env("ASCNET_PATCH_ORIGIN", &options.origin);
    apply_patch_options(&mut command, options);
    if running_under_wine() {
        let old = std::env::var("WINEDLLOVERRIDES").unwrap_or_default();
        let merged = if old.is_empty() {
            "version=n,b".into()
        } else {
            format!("{old};version=n,b")
        };
        command.env("WINEDLLOVERRIDES", merged);
    }
    command.spawn().context("launching PGR.exe")?;
    Ok(())
}

fn apply_patch_options(command: &mut Command, options: &LaunchOptions) {
    if options.no_camera_fade {
        command.env("ASCNET_PATCH_NOFADE", "1");
    } else {
        command.env_remove("ASCNET_PATCH_NOFADE");
    }
    match options.fps {
        Some(fps) => command.env("ASCNET_PATCH_FPS", fps.to_string()),
        None => command.env_remove("ASCNET_PATCH_FPS"),
    };
}

fn running_under_wine() -> bool {
    use windows::core::{s, w};
    use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};

    unsafe {
        GetModuleHandleW(w!("ntdll.dll"))
            .ok()
            .and_then(|module| GetProcAddress(module, s!("wine_get_version")))
            .is_some()
    }
}

// ───────────────────────── Wine (not used on Windows) ─────────────────────────

pub fn wine_candidates() -> Vec<PathBuf> {
    Vec::new()
}

pub fn proton_candidates(_steam_root: &Path) -> Vec<PathBuf> {
    Vec::new()
}

/// The Windows client runs natively here; the GUI asks for a runtime before every launch.
pub fn ensure_managed_wine(_progress: &mut dyn FnMut(&str)) -> Result<GameRuntime> {
    Ok(GameRuntime::Native)
}

// ───────────────────────── Steam discovery ─────────────────────────

pub fn steam_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    if let Some(path) = std::env::var_os("STEAM_PATH") {
        roots.push(path.into());
    }
    for key in ["ProgramFiles(x86)", "ProgramFiles"] {
        if let Some(path) = std::env::var_os(key) {
            roots.push(PathBuf::from(path).join("Steam"));
        }
    }
    roots.sort();
    roots.dedup();
    roots
}

pub fn steam_installs() -> Vec<SteamInstall> {
    let mut installs = Vec::new();
    for root in steam_roots() {
        for library in super::steam_libraries(&root) {
            let apps = library.join("steamapps");
            let Ok(entries) = fs::read_dir(&apps) else {
                continue;
            };
            for entry in entries.flatten() {
                let name = entry.file_name();
                let name = name.to_string_lossy();
                if !name.starts_with("appmanifest_") || !name.ends_with(".acf") {
                    continue;
                }
                let Ok(text) = fs::read_to_string(entry.path()) else {
                    continue;
                };
                let Ok(fields) = super::vdf_object_fields(&text, "AppState") else {
                    continue;
                };
                let Some(dir) = fields.get("installdir") else {
                    continue;
                };
                let game = apps.join("common").join(dir);
                if game.join("PGR.exe").is_file() {
                    let app_id = name
                        .trim_start_matches("appmanifest_")
                        .trim_end_matches(".acf")
                        .to_owned();
                    installs.push(SteamInstall {
                        game,
                        app_id: Some(app_id),
                        runtime: GameRuntime::Native,
                    });
                }
            }
        }
    }
    installs
}
