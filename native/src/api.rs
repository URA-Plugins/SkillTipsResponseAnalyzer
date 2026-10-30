use std::ffi::{CStr, c_char, c_void};
use std::sync::OnceLock;

pub type GetApi = unsafe extern "C" fn(*const c_char) -> *mut c_void;
pub type Callback = unsafe extern "C" fn();
type Ptr = usize;

pub struct Api {
    base_dir: unsafe extern "C" fn() -> *const c_char,
    pub main_thread: Ptr,
    schedule: unsafe extern "C" fn(Ptr, Callback),
    image: unsafe extern "C" fn(*const c_char) -> Ptr,
    class: unsafe extern "C" fn(Ptr, *const c_char, *const c_char) -> Ptr,
    pub class_parent: unsafe extern "C" fn(Ptr) -> Ptr,
    pub class_methods: unsafe extern "C" fn(Ptr, *mut Ptr) -> Ptr,
    pub method_name: unsafe extern "C" fn(Ptr) -> *const c_char,
    pub method_argc: unsafe extern "C" fn(Ptr) -> u32,
    nested: unsafe extern "C" fn(Ptr, *mut Ptr) -> Ptr,
    class_name: unsafe extern "C" fn(Ptr) -> *const c_char,
    pub field: unsafe extern "C" fn(Ptr, *const c_char) -> Ptr,
    pub static_field_get: unsafe extern "C" fn(Ptr, *mut c_void),
    pub object_class: unsafe extern "C" fn(Ptr) -> Ptr,
    pub object_new: unsafe extern "C" fn(Ptr) -> Ptr,
    invoke: unsafe extern "C" fn(Ptr, Ptr, *mut Ptr, *mut Ptr) -> Ptr,
    invoke_objects: unsafe extern "C" fn(Ptr, Ptr, *mut Ptr, i32, *mut Ptr) -> Ptr,
    pub handle_new: unsafe extern "C" fn(Ptr, bool) -> u32,
    pub handle_target: unsafe extern "C" fn(u32) -> Ptr,
    pub handle_free: unsafe extern "C" fn(u32),
    hook: unsafe extern "C" fn(Ptr, Ptr, Ptr) -> Ptr,
    unhook: unsafe extern "C" fn(Ptr, Ptr) -> Ptr,
    trampoline: unsafe extern "C" fn(Ptr, Ptr) -> Ptr,
    interceptor: Ptr,
    log: unsafe extern "C" fn(i32, *const c_char, *const c_char),
}

static API: OnceLock<Api> = OnceLock::new();

pub fn get() -> &'static Api {
    API.get().expect("Hachimi API 尚未初始化")
}

pub unsafe fn initialize(get_api: GetApi, version: i32) -> Result<(), String> {
    if version < 3 {
        return Err(format!("需要 Hachimi plugin API v3，收到 v{version}"));
    }
    let fetch = |name: &CStr| -> Result<Ptr, String> {
        let pointer = unsafe { get_api(name.as_ptr()) } as Ptr;
        if pointer == 0 {
            Err(format!("Hachimi API 缺少 {}", name.to_string_lossy()))
        } else {
            Ok(pointer)
        }
    };
    let resolve: unsafe extern "C" fn(*const c_char) -> Ptr =
        unsafe { std::mem::transmute(fetch(c"il2cpp_resolve_symbol")?) };
    let symbol = |name: &CStr| -> Result<Ptr, String> {
        let pointer = unsafe { resolve(name.as_ptr()) };
        if pointer == 0 {
            Err(format!("IL2CPP 缺少 {}", name.to_string_lossy()))
        } else {
            Ok(pointer)
        }
    };
    macro_rules! api {
        ($name:literal) => {
            unsafe { std::mem::transmute(fetch($name)?) }
        };
    }
    macro_rules! il2cpp {
        ($name:literal) => {
            unsafe { std::mem::transmute(symbol($name)?) }
        };
    }
    let instance: unsafe extern "C" fn() -> Ptr = api!(c"hachimi_instance");
    let interceptor: unsafe extern "C" fn(Ptr) -> Ptr = api!(c"hachimi_get_interceptor");
    let main_thread: unsafe extern "C" fn() -> Ptr = api!(c"il2cpp_get_main_thread");
    let api = Api {
        base_dir: api!(c"hachimi_get_base_dir"),
        main_thread: unsafe { main_thread() },
        schedule: api!(c"il2cpp_schedule_on_thread"),
        image: api!(c"il2cpp_get_assembly_image"),
        class: api!(c"il2cpp_get_class"),
        class_parent: il2cpp!(c"il2cpp_class_get_parent"),
        class_methods: il2cpp!(c"il2cpp_class_get_methods"),
        method_name: il2cpp!(c"il2cpp_method_get_name"),
        method_argc: il2cpp!(c"il2cpp_method_get_param_count"),
        nested: il2cpp!(c"il2cpp_class_get_nested_types"),
        class_name: il2cpp!(c"il2cpp_class_get_name"),
        field: il2cpp!(c"il2cpp_class_get_field_from_name"),
        static_field_get: il2cpp!(c"il2cpp_field_static_get_value"),
        object_class: il2cpp!(c"il2cpp_object_get_class"),
        object_new: api!(c"il2cpp_object_new"),
        invoke: il2cpp!(c"il2cpp_runtime_invoke"),
        invoke_objects: il2cpp!(c"il2cpp_runtime_invoke_convert_args"),
        handle_new: il2cpp!(c"il2cpp_gchandle_new"),
        handle_target: il2cpp!(c"il2cpp_gchandle_get_target"),
        handle_free: il2cpp!(c"il2cpp_gchandle_free"),
        hook: api!(c"interceptor_hook"),
        unhook: api!(c"interceptor_unhook"),
        trampoline: api!(c"interceptor_get_trampoline_addr"),
        interceptor: unsafe { interceptor(instance()) },
        log: api!(c"log"),
    };
    if api.main_thread == 0 || api.interceptor == 0 {
        return Err("Hachimi 主线程或 interceptor 为空".into());
    }
    API.set(api).map_err(|_| "原生插件重复初始化".to_owned())
}

impl Api {
    pub fn base_dir(&self) -> Result<std::path::PathBuf, String> {
        let pointer = unsafe { (self.base_dir)() };
        if pointer.is_null() {
            return Err("hachimi_get_base_dir 返回空地址".into());
        }
        let value = unsafe { CStr::from_ptr(pointer) }
            .to_str()
            .map_err(|error| format!("Hachimi 基目录不是 UTF-8: {error}"))?;
        if value.is_empty() {
            return Err("Hachimi 基目录为空".into());
        }
        Ok(value.into())
    }
    pub fn schedule(&self, callback: Callback) {
        unsafe { (self.schedule)(self.main_thread, callback) }
    }
    pub fn class(&self, assembly: &CStr, namespace: &CStr, name: &CStr) -> Result<Ptr, String> {
        let image = unsafe { (self.image)(assembly.as_ptr()) };
        if image == 0 {
            return Err(format!(
                "IL2CPP image {} 不存在",
                assembly.to_string_lossy()
            ));
        }
        let class = unsafe { (self.class)(image, namespace.as_ptr(), name.as_ptr()) };
        if class == 0 {
            return Err(format!(
                "IL2CPP class {}.{} 不存在",
                namespace.to_string_lossy(),
                name.to_string_lossy()
            ));
        }
        Ok(class)
    }
    pub fn singleton_field(&self, class: Ptr) -> Result<Ptr, String> {
        let parent = unsafe { (self.class_parent)(class) };
        let field = unsafe { (self.field)(parent, c"_instance".as_ptr()) };
        if field == 0 {
            return Err(format!("class 0x{class:X} 的父类缺少静态 _instance"));
        }
        Ok(field)
    }
    pub fn nested_class(&self, parent: Ptr, name: &str) -> Result<Ptr, String> {
        let mut iterator = 0;
        loop {
            let class = unsafe { (self.nested)(parent, &mut iterator) };
            if class == 0 {
                return Err(format!("嵌套类型 {name} 不存在"));
            }
            if unsafe { CStr::from_ptr((self.class_name)(class)) }.to_bytes() == name.as_bytes() {
                return Ok(class);
            }
        }
    }
    pub fn existing_singleton(&self, field: Ptr) -> Ptr {
        let mut value = 0;
        unsafe { (self.static_field_get)(field, (&mut value as *mut Ptr).cast()) };
        value
    }
    pub fn invoke(
        &self,
        method: Ptr,
        this: Ptr,
        objects: &mut [Ptr],
        name: &str,
    ) -> Result<Ptr, String> {
        let mut exception = 0;
        let result = unsafe {
            if objects.is_empty() {
                (self.invoke)(method, this, std::ptr::null_mut(), &mut exception)
            } else {
                (self.invoke_objects)(
                    method,
                    this,
                    objects.as_mut_ptr(),
                    objects.len() as i32,
                    &mut exception,
                )
            }
        };
        if exception != 0 {
            return Err(format!(
                "{name} 抛出 IL2CPP exception 0x{exception:X}，MethodInfo=0x{method:X}"
            ));
        }
        Ok(result)
    }
    pub fn hook(&self, address: Ptr, replacement: Ptr) -> Result<(), String> {
        if unsafe { (self.hook)(self.interceptor, address, replacement) } == 0 {
            return Err(format!("hook 0x{address:X} 失败"));
        }
        if self.trampoline(replacement) == 0 {
            self.unhook(replacement);
            return Err(format!("hook 0x{address:X} 没有 trampoline"));
        }
        Ok(())
    }
    pub fn unhook(&self, replacement: Ptr) {
        unsafe {
            (self.unhook)(self.interceptor, replacement);
        }
    }
    pub fn trampoline(&self, replacement: Ptr) -> Ptr {
        unsafe { (self.trampoline)(self.interceptor, replacement) }
    }
}

pub fn log(message: &str) {
    if let Some(api) = API.get() {
        let text = std::ffi::CString::new(message.replace('\0', " ")).unwrap();
        unsafe { (api.log)(2, c"skill_tips_bridge".as_ptr(), text.as_ptr()) }
    }
}

pub struct GcRoot(u32);
impl GcRoot {
    pub fn new(object: Ptr) -> Result<Self, String> {
        if object == 0 {
            return Err("无法保活空 IL2CPP 对象".into());
        }
        let handle = unsafe { (get().handle_new)(object, false) };
        if handle == 0 {
            return Err("il2cpp_gchandle_new 返回 0".into());
        }
        Ok(Self(handle))
    }
    pub fn object(&self) -> Ptr {
        unsafe { (get().handle_target)(self.0) }
    }
}
impl Drop for GcRoot {
    fn drop(&mut self) {
        unsafe { (get().handle_free)(self.0) }
    }
}
