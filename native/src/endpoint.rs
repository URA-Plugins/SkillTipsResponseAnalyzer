use serde::Deserialize;
use std::{net::IpAddr, path::Path};
use windows::Win32::Networking::WinHttp::{
    URL_COMPONENTS, WINHTTP_INTERNET_SCHEME_HTTP, WINHTTP_INTERNET_SCHEME_HTTPS, WinHttpCrackUrl,
};

pub const ROUTE: &str = "/SkillTipsResponseAnalyzer/bridge";

#[derive(Debug, PartialEq, Eq)]
pub struct Endpoint {
    pub host: String,
    pub port: u16,
    pub path: String,
    pub secure: bool,
}

#[derive(Deserialize)]
struct NotifierSettings {
    notifier_host: String,
}

impl Endpoint {
    pub fn load(base_dir: &Path) -> Result<Self, String> {
        let path = base_dir.join("httpforward.json");
        let text = std::fs::read_to_string(&path)
            .map_err(|error| format!("读取 {}: {error}", path.display()))?;
        Self::from_json(&text).map_err(|error| format!("{}: {error}", path.display()))
    }

    fn from_json(text: &str) -> Result<Self, String> {
        let settings: NotifierSettings = serde_json::from_str(text)
            .map_err(|error| format!("notifier_host 配置无效: {error}"))?;
        Self::parse(&settings.notifier_host)
    }

    pub fn parse(url: &str) -> Result<Self, String> {
        if url.is_empty() || url.contains('\0') || url.trim() != url {
            return Err("notifier_host 必须是非空 HTTP/HTTPS URL，不能包含首尾空白或空字符".into());
        }
        let url: Vec<u16> = url.encode_utf16().collect();
        let mut components = URL_COMPONENTS {
            dwStructSize: size_of::<URL_COMPONENTS>() as u32,
            dwSchemeLength: u32::MAX,
            dwHostNameLength: u32::MAX,
            dwUserNameLength: u32::MAX,
            dwPasswordLength: u32::MAX,
            dwUrlPathLength: u32::MAX,
            dwExtraInfoLength: u32::MAX,
            ..Default::default()
        };
        unsafe { WinHttpCrackUrl(&url, 0, &mut components) }
            .map_err(|error| format!("解析 notifier_host: {error}"))?;
        let secure = match components.nScheme {
            WINHTTP_INTERNET_SCHEME_HTTP => false,
            WINHTTP_INTERNET_SCHEME_HTTPS => true,
            _ => return Err("notifier_host 只接受 http:// 或 https://".into()),
        };
        if components.dwUserNameLength != 0
            || components.dwPasswordLength != 0
            || components.dwExtraInfoLength != 0
        {
            return Err("notifier_host 不能包含凭据、查询参数或片段".into());
        }
        let host = utf16_component(components.lpszHostName.0, components.dwHostNameLength)?;
        let host = host
            .trim_start_matches('[')
            .trim_end_matches(']')
            .to_owned();
        if !host.eq_ignore_ascii_case("localhost")
            && !host
                .parse::<IpAddr>()
                .is_ok_and(|address| address.is_loopback())
        {
            return Err("notifier_host 必须指向 localhost 或回环 IP 地址".into());
        }
        if components.nPort == 0 {
            return Err("notifier_host 端口必须为正数".into());
        }
        let base_path = utf16_component(components.lpszUrlPath.0, components.dwUrlPathLength)?;
        Ok(Self {
            host,
            port: components.nPort,
            path: format!("{}{ROUTE}", base_path.trim_end_matches('/')),
            secure,
        })
    }
}

fn utf16_component(pointer: *const u16, length: u32) -> Result<String, String> {
    if length == 0 {
        return Ok(String::new());
    }
    String::from_utf16(unsafe { std::slice::from_raw_parts(pointer, length as usize) })
        .map_err(|error| format!("URL 包含无效 UTF-16: {error}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn configured_endpoint_preserves_port_path_and_transport() {
        let endpoint = Endpoint::from_json(
            r#"{"notifier_host":"http://127.0.0.1:4693/ura/","notifier_timeout_ms":100}"#,
        )
        .unwrap();
        assert_eq!(
            endpoint,
            Endpoint {
                host: "127.0.0.1".into(),
                port: 4693,
                path: format!("/ura{ROUTE}"),
                secure: false,
            }
        );
        assert_eq!(Endpoint::parse("https://localhost").unwrap().port, 443);
        assert!(Endpoint::parse("https://[::1]:5443/base").unwrap().secure);
        assert!(Endpoint::parse("http://127.25.1.2:1234").is_ok());
    }

    #[test]
    fn invalid_or_missing_configuration_is_not_repaired() {
        for text in [
            "{}",
            "null",
            r#"{"notifier_host":2}"#,
            r#"{"notifier_host":"http://localhost","notifier_host":"http://127.0.0.1"}"#,
        ] {
            assert!(Endpoint::from_json(text).is_err(), "{text}");
        }
        for url in [
            "",
            " http://localhost",
            "ws://localhost",
            "http://example.com",
            "http://192.168.1.2",
            "http://[::2]",
            "http://user:pass@localhost",
            "http://localhost/?query=1",
            "http://localhost/#fragment",
            "http://localhost:0",
            "http://localhost:99999",
        ] {
            assert!(Endpoint::parse(url).is_err(), "{url}");
        }
        let missing =
            std::env::temp_dir().join(format!("skill-tips-missing-{}", std::process::id()));
        assert!(
            Endpoint::load(&missing)
                .unwrap_err()
                .contains("httpforward.json")
        );
    }
}
