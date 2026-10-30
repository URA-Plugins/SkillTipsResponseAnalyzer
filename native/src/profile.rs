use crate::{api, memory};
use serde::Deserialize;
use std::{
    collections::HashMap,
    ffi::{CStr, CString},
};
use windows::{Win32::System::LibraryLoader::GetModuleHandleW, core::w};

#[derive(Deserialize)]
pub struct Profile {
    pub profile: String,
    pub game_version: String,
    pub unity_version: String,
    pub methods: Vec<MethodSpec>,
}
#[derive(Deserialize)]
pub struct MethodSpec {
    pub key: String,
    pub namespace: String,
    pub class: String,
    pub name: String,
    pub argc: i32,
    pub rva: usize,
    pub prologue: String,
}
#[derive(Clone, Copy)]
pub struct Method {
    pub info: usize,
    pub address: usize,
}
pub struct Methods {
    pub items: HashMap<String, Method>,
    pub model_class: usize,
    pub controller_class: usize,
    pub scene_field: usize,
    pub work_field: usize,
}

pub fn parse() -> Result<Profile, String> {
    serde_json::from_str(include_str!("../profile.json")).map_err(|e| format!("profile.json: {e}"))
}

pub fn class(namespace: &str, name: &str) -> Result<usize, String> {
    let mut segments = name.split('/');
    let namespace = CString::new(namespace).map_err(|e| e.to_string())?;
    let outer = CString::new(segments.next().unwrap()).map_err(|e| e.to_string())?;
    let assembly = if namespace.as_bytes() == b"CodeStage.AntiCheat.ObscuredTypes" {
        c"Plugins.dll"
    } else {
        c"umamusume.dll"
    };
    let mut class = api::get().class(assembly, &namespace, &outer)?;
    for nested in segments {
        class = api::get().nested_class(class, nested)?;
    }
    Ok(class)
}

impl Methods {
    pub fn load() -> Result<Self, String> {
        let profile = parse()?;
        if profile.profile != crate::protocol::PROFILE {
            return Err("profile 标识不一致".into());
        }
        let base = unsafe { GetModuleHandleW(w!("GameAssembly.dll")) }
            .map_err(|e| format!("GameAssembly.dll 未加载: {e}"))?
            .0 as usize;
        let mut items = HashMap::new();
        for spec in profile.methods {
            let class = class(&spec.namespace, &spec.class)?;
            let address = base + spec.rva;
            let expected = hex(&spec.prologue)?;
            if memory::bytes(address, expected.len())? != expected {
                return Err(format!(
                    "profile {}: {}.{} RVA 0x{:X} 的机器码不匹配；需要重新核验游戏版本",
                    profile.profile, spec.class, spec.name, spec.rva
                ));
            }
            let mut iterator = 0;
            let mut matched = vec![];
            loop {
                let method = unsafe { (api::get().class_methods)(class, &mut iterator) };
                if method == 0 {
                    break;
                }
                let name = unsafe { CStr::from_ptr((api::get().method_name)(method)) };
                if name.to_bytes() == spec.name.as_bytes()
                    && unsafe { (api::get().method_argc)(method) } == spec.argc as u32
                    && memory::pointer(method, 0)? == address
                {
                    matched.push(method);
                }
            }
            if matched.len() != 1 {
                return Err(format!(
                    "{}.{}({}) RVA 0x{:X} 匹配 {} 个 MethodInfo，必须唯一",
                    spec.class,
                    spec.name,
                    spec.argc,
                    spec.rva,
                    matched.len()
                ));
            }
            let info = matched[0];
            items.insert(spec.key, Method { info, address });
        }
        api::log(&format!(
            "profile {}，游戏 {}，Unity {}，{} 个方法校验通过",
            profile.profile,
            profile.game_version,
            profile.unity_version,
            items.len()
        ));
        Ok(Self {
            items,
            model_class: class("Gallop", "SingleModeSkillLearningModel")?,
            controller_class: class("Gallop", "SingleModeSkillLearningViewController")?,
            scene_field: api::get().singleton_field(class("Gallop", "SceneManager")?)?,
            work_field: api::get().singleton_field(class("Gallop", "WorkDataManager")?)?,
        })
    }
    pub fn invoke(&self, key: &str, this: usize, objects: &mut [usize]) -> Result<usize, String> {
        api::get().invoke(self.items[key].info, this, objects, key)
    }
    pub fn int(&self, key: &str, this: usize) -> Result<i32, String> {
        memory::int(self.invoke(key, this, &mut [])?, 0x10)
    }
}

fn hex(text: &str) -> Result<Vec<u8>, String> {
    if text.is_empty() || !text.len().is_multiple_of(2) {
        return Err("profile prologue 长度无效".into());
    }
    (0..text.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&text[i..i + 2], 16).map_err(|e| e.to_string()))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn profile_is_complete_and_contains_no_learning_entry() {
        let p = parse().unwrap();
        let mut keys = std::collections::HashSet::new();
        for method in &p.methods {
            assert!(keys.insert(&method.key));
            assert_eq!(hex(&method.prologue).unwrap().len(), 16);
            assert!(method.rva > 0);
            assert!(!method.name.contains("Decide"));
            assert!(!method.name.contains("SkillGet"));
        }
        for key in [
            "model_ctor",
            "select",
            "reset",
            "remaining",
            "begin",
            "out",
            "update",
            "current_view",
            "transition",
            "chara_id",
            "start_time",
            "turn",
            "points",
            "unbox_int",
        ] {
            assert!(keys.iter().any(|k| k.as_str() == key));
        }
    }
}
