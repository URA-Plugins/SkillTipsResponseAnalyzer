use crate::{api, memory, profile::Methods};
use std::ffi::c_void;

pub fn install(methods: &Methods) -> Result<(), String> {
    let hooks = [
        ("begin", begin as *const () as usize),
        ("out", out as *const () as usize),
        ("select", select as *const () as usize),
        ("deselect", deselect as *const () as usize),
        ("reset", reset as *const () as usize),
        ("sort", sort as *const () as usize),
    ];
    let mut installed = vec![];
    for (key, replacement) in hooks {
        if let Err(error) = api::get().hook(methods.items[key].address, replacement) {
            for previous in installed.into_iter().rev() {
                api::get().unhook(previous);
            }
            return Err(format!("安装 {key}: {error}"));
        }
        installed.push(replacement);
    }
    Ok(())
}

extern "C-unwind" fn begin(this: usize, method: *const c_void) {
    let original: extern "C-unwind" fn(usize, *const c_void) =
        unsafe { std::mem::transmute(api::get().trampoline(begin as *const () as usize)) };
    original(this, method);
    let mut schedule = false;
    crate::with_runtime(|runtime| schedule = runtime.entered(this));
    // BeginView returns before the surrounding scene transition finishes.
    if schedule {
        api::get().schedule(crate::poll_entry);
    }
}

extern "C-unwind" fn out(this: usize, method: *const c_void) -> usize {
    crate::with_runtime(|runtime| runtime.leaving(this));
    let original: extern "C-unwind" fn(usize, *const c_void) -> usize =
        unsafe { std::mem::transmute(api::get().trampoline(out as *const () as usize)) };
    original(this, method)
}

extern "C-unwind" fn select(this: usize, info: usize, method: *const c_void) {
    let before = memory::pointer(this, 0x10).and_then(memory::ids);
    let original: extern "C-unwind" fn(usize, usize, *const c_void) =
        unsafe { std::mem::transmute(api::get().trampoline(select as *const () as usize)) };
    original(this, info, method);
    crate::with_runtime(|runtime| {
        let after = memory::pointer(this, 0x10).and_then(memory::ids);
        let added = matches!((&before, &after), (Ok(before), Ok(after)) if after.iter().any(|id| before.binary_search(id).is_err()));
        runtime.changed(this, added);
    });
}

extern "C-unwind" fn deselect(this: usize, info: usize, method: *const c_void) -> usize {
    let original: extern "C-unwind" fn(usize, usize, *const c_void) -> usize =
        unsafe { std::mem::transmute(api::get().trampoline(deselect as *const () as usize)) };
    let result = original(this, info, method);
    crate::with_runtime(|runtime| runtime.changed(this, false));
    result
}

extern "C-unwind" fn reset(this: usize, method: *const c_void) {
    let original: extern "C-unwind" fn(usize, *const c_void) =
        unsafe { std::mem::transmute(api::get().trampoline(reset as *const () as usize)) };
    original(this, method);
    crate::with_runtime(|runtime| runtime.changed(this, false));
}

extern "C-unwind" fn sort(this: usize, sort: usize, filter: usize, method: *const c_void) {
    let original: extern "C-unwind" fn(usize, usize, usize, *const c_void) =
        unsafe { std::mem::transmute(api::get().trampoline(self::sort as *const () as usize)) };
    original(this, sort, filter, method);
    crate::with_runtime(|runtime| runtime.changed(this, false));
}
