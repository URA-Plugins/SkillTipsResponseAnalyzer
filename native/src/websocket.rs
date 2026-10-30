use crate::{endpoint::Endpoint, protocol::MAX_MESSAGE};
use std::{
    ffi::c_void,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
        mpsc,
    },
    thread,
    time::Duration,
};
use windows::{
    Win32::Networking::WinHttp::*,
    core::{PCWSTR, w},
};

struct Handle(AtomicUsize);

impl Handle {
    fn new(pointer: *mut c_void, operation: &str) -> Result<Self, String> {
        if pointer.is_null() {
            return Err(format!(
                "{operation}: {}",
                windows::core::Error::from_thread()
            ));
        }
        Ok(Self(AtomicUsize::new(pointer as usize)))
    }

    fn raw(&self) -> *mut c_void {
        self.0.load(Ordering::Acquire) as *mut c_void
    }

    fn close(&self) {
        let pointer = self.0.swap(0, Ordering::AcqRel) as *mut c_void;
        if !pointer.is_null() {
            // Closing the handle cancels an outstanding blocking receive. There is
            // one reader and one writer; neither holds a mutex across WinHTTP calls.
            let _ = unsafe { WinHttpCloseHandle(pointer) };
        }
    }
}

impl Drop for Handle {
    fn drop(&mut self) {
        self.close();
    }
}

pub struct Socket {
    websocket: Handle,
    _connection: Handle,
    _session: Handle,
}

impl Socket {
    #[cfg(test)]
    pub(crate) fn closed_for_test() -> Self {
        Self {
            websocket: Handle(AtomicUsize::new(0)),
            _connection: Handle(AtomicUsize::new(0)),
            _session: Handle(AtomicUsize::new(0)),
        }
    }

    pub fn connect(endpoint: &Endpoint) -> Result<Self, String> {
        let session = Handle::new(
            unsafe {
                WinHttpOpen(
                    w!("SkillTipsResponseAnalyzer/2"),
                    WINHTTP_ACCESS_TYPE_NO_PROXY,
                    PCWSTR::null(),
                    PCWSTR::null(),
                    0,
                )
            },
            "WinHttpOpen",
        )?;
        unsafe { WinHttpSetTimeouts(session.raw(), 5000, 5000, 5000, 5000) }
            .map_err(|error| format!("设置握手超时: {error}"))?;
        let host = wide(&endpoint.host);
        let connection = Handle::new(
            unsafe { WinHttpConnect(session.raw(), PCWSTR(host.as_ptr()), endpoint.port, 0) },
            "WinHttpConnect",
        )?;
        let path = wide(&endpoint.path);
        let flags = if endpoint.secure {
            WINHTTP_FLAG_SECURE
        } else {
            WINHTTP_OPEN_REQUEST_FLAGS(0)
        };
        let request = Arc::new(Handle::new(
            unsafe {
                WinHttpOpenRequest(
                    connection.raw(),
                    w!("GET"),
                    PCWSTR(path.as_ptr()),
                    PCWSTR::null(),
                    PCWSTR::null(),
                    std::ptr::null(),
                    flags,
                )
            },
            "WinHttpOpenRequest",
        )?);
        unsafe {
            WinHttpSetOption(
                Some(request.raw()),
                WINHTTP_OPTION_REDIRECT_POLICY,
                Some(&WINHTTP_OPTION_REDIRECT_POLICY_NEVER.to_ne_bytes()),
            )
        }
        .map_err(|error| format!("禁止重定向: {error}"))?;
        unsafe {
            WinHttpSetOption(
                Some(request.raw()),
                WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET,
                None,
            )
        }
        .map_err(|error| format!("启用 WebSocket upgrade: {error}"))?;

        // The five-second deadline covers send + receive together, not each stage.
        let (finished, deadline) = mpsc::channel::<()>();
        let timed_request = request.clone();
        let timer = thread::Builder::new()
            .name("skill-tips-handshake".into())
            .spawn(move || {
                if deadline.recv_timeout(Duration::from_secs(5)).is_err() {
                    timed_request.close();
                    true
                } else {
                    false
                }
            })
            .map_err(|error| format!("创建握手计时线程: {error}"))?;
        let result = (|| {
            unsafe { WinHttpSendRequest(request.raw(), None, None, 0, 0, 0) }
                .map_err(|error| format!("发送 WebSocket 握手: {error}"))?;
            unsafe { WinHttpReceiveResponse(request.raw(), std::ptr::null_mut()) }
                .map_err(|error| format!("读取 WebSocket 握手: {error}"))?;
            let mut status = 0u32;
            let mut length = size_of::<u32>() as u32;
            unsafe {
                WinHttpQueryHeaders(
                    request.raw(),
                    WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                    PCWSTR::null(),
                    Some((&mut status as *mut u32).cast()),
                    &mut length,
                    std::ptr::null_mut(),
                )
            }
            .map_err(|error| format!("读取握手 HTTP 状态: {error}"))?;
            if status != 101 {
                return Err(format!(
                    "WebSocket 握手返回 HTTP {status}，要求 101；端点 {}:{}{}",
                    endpoint.host, endpoint.port, endpoint.path
                ));
            }
            // Set request options before upgrading: the WebSocket handle does not
            // accept WINHTTP_OPTION_RECEIVE_TIMEOUT (WinHTTP 12018).
            unsafe {
                WinHttpSetOption(
                    Some(request.raw()),
                    WINHTTP_OPTION_RECEIVE_TIMEOUT,
                    Some(&0u32.to_ne_bytes()),
                )
            }
            .map_err(|error| format!("设置 WebSocket 空闲接收: {error}"))?;
            Handle::new(
                unsafe { WinHttpWebSocketCompleteUpgrade(request.raw(), None) },
                "完成 WebSocket upgrade",
            )
        })();
        let _ = finished.send(());
        if timer.join().map_err(|_| "握手计时线程发生 panic")? {
            return Err("WebSocket 握手超过 5 秒，连接已取消".into());
        }
        let websocket = result?;
        Ok(Self {
            websocket,
            _connection: connection,
            _session: session,
        })
    }

    pub fn close(&self) {
        self.websocket.close();
    }

    pub fn send(&self, bytes: &[u8]) -> Result<(), String> {
        let code = unsafe {
            WinHttpWebSocketSend(
                self.websocket.raw(),
                WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE,
                Some(bytes),
            )
        };
        if code != 0 {
            return Err(format!("WebSocket 发送失败，Win32={code}"));
        }
        Ok(())
    }

    pub fn receive(&self) -> Result<Option<Vec<u8>>, String> {
        let mut message = Message::default();
        let mut buffer = [0u8; 4096];
        loop {
            let mut read = 0;
            let mut kind = WINHTTP_WEB_SOCKET_BUFFER_TYPE::default();
            let code = unsafe {
                WinHttpWebSocketReceive(
                    self.websocket.raw(),
                    buffer.as_mut_ptr().cast(),
                    buffer.len() as u32,
                    &mut read,
                    &mut kind,
                )
            };
            if code != 0 {
                return Err(format!("WebSocket 接收失败，Win32={code}"));
            }
            if kind == WINHTTP_WEB_SOCKET_CLOSE_BUFFER_TYPE {
                return Ok(None);
            }
            match kind {
                WINHTTP_WEB_SOCKET_UTF8_FRAGMENT_BUFFER_TYPE => {
                    message.push(&buffer[..read as usize], false)?;
                }
                WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE => {
                    return message.push(&buffer[..read as usize], true);
                }
                _ => return Err(format!("WebSocket 只接受文本消息，收到类型 {}", kind.0)),
            }
        }
    }
}

fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

#[derive(Default)]
struct Message(Vec<u8>);

impl Message {
    fn push(&mut self, fragment: &[u8], complete: bool) -> Result<Option<Vec<u8>>, String> {
        if self.0.len() + fragment.len() > MAX_MESSAGE {
            return Err(format!("WebSocket 消息超过 {MAX_MESSAGE} 字节"));
        }
        self.0.extend_from_slice(fragment);
        if !complete {
            return Ok(None);
        }
        std::str::from_utf8(&self.0)
            .map_err(|error| format!("WebSocket 文本不是有效 UTF-8: {error}"))?;
        Ok(Some(std::mem::take(&mut self.0)))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fragmented_text_preserves_utf8_and_message_boundaries() {
        let bytes = "{\"message\":\"技能\"}".as_bytes();
        let split = bytes.iter().position(|b| *b == 0xe6).unwrap() + 1;
        let mut message = Message::default();
        assert!(message.push(&bytes[..split], false).unwrap().is_none());
        assert_eq!(message.push(&bytes[split..], true).unwrap().unwrap(), bytes);
        assert_eq!(message.push(b"{}", true).unwrap().unwrap(), b"{}");
        assert!(message.push(&[0xff], true).is_err());
    }

    #[test]
    fn message_limit_counts_all_fragments() {
        let mut message = Message::default();
        assert!(
            message
                .push(&vec![b'a'; MAX_MESSAGE - 1], false)
                .unwrap()
                .is_none()
        );
        assert_eq!(
            message.push(b"a", true).unwrap().unwrap().len(),
            MAX_MESSAGE
        );
        message.push(&vec![b'a'; MAX_MESSAGE], false).unwrap();
        assert!(message.push(b"a", true).is_err());
    }

    #[test]
    fn handshake_rejects_non_upgrade_response_without_following_redirect() {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let endpoint = Endpoint::parse(&format!(
            "http://127.0.0.1:{}",
            listener.local_addr().unwrap().port()
        ))
        .unwrap();
        let peer = thread::spawn(move || {
            let (mut connection, _) = listener.accept().unwrap();
            let mut bytes = [0; 4096];
            connection.read(&mut bytes).unwrap();
            connection.write_all(b"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").unwrap();
        });
        let error = Socket::connect(&endpoint).err().unwrap();
        peer.join().unwrap();
        assert!(error.contains("HTTP 302"), "{error}");
    }

    #[test]
    fn stalled_handshake_is_cancelled_at_the_total_deadline() {
        use std::io::Read;
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let endpoint = Endpoint::parse(&format!(
            "http://127.0.0.1:{}",
            listener.local_addr().unwrap().port()
        ))
        .unwrap();
        let peer = thread::spawn(move || {
            let (mut connection, _) = listener.accept().unwrap();
            connection
                .set_read_timeout(Some(Duration::from_secs(8)))
                .unwrap();
            let mut buffer = [0; 4096];
            while connection.read(&mut buffer).is_ok_and(|read| read != 0) {}
        });
        let start = std::time::Instant::now();
        let error = Socket::connect(&endpoint).err().unwrap();
        assert!(
            error.contains("5 秒") || error.contains("0x80072EE2"),
            "{error}"
        );
        assert!(start.elapsed() < Duration::from_secs(7));
        peer.join().unwrap();
    }

    #[test]
    fn upgraded_socket_receives_fragments_sends_text_and_aborts_blocked_receive() {
        use std::{
            io::{BufRead, BufReader},
            os::windows::process::CommandExt,
            process::{Child, Command, Stdio},
        };
        struct Peer(Child);
        impl Drop for Peer {
            fn drop(&mut self) {
                let _ = self.0.kill();
                let _ = self.0.wait();
            }
        }
        let probe = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let port = probe.local_addr().unwrap().port();
        drop(probe);
        let mut peer = Peer(
            Command::new("pwsh")
                .args(["-NoProfile", "-File"])
                .arg(concat!(
                    env!("CARGO_MANIFEST_DIR"),
                    "/tests/websocket_peer.ps1"
                ))
                .arg("-Port")
                .arg(port.to_string())
                .creation_flags(0x08000000)
                .stdout(Stdio::piped())
                .stderr(Stdio::inherit())
                .spawn()
                .unwrap(),
        );
        let mut output = BufReader::new(peer.0.stdout.take().unwrap());
        let mut line = String::new();
        output.read_line(&mut line).unwrap();
        assert_eq!(line.trim(), "READY");
        let endpoint = Endpoint::parse(&format!("http://127.0.0.1:{port}")).unwrap();
        let socket = Arc::new(Socket::connect(&endpoint).unwrap());
        assert_eq!(socket.receive().unwrap().unwrap(), "技能".as_bytes());
        socket.send(br#"{"kind":"test"}"#).unwrap();
        line.clear();
        output.read_line(&mut line).unwrap();
        assert_eq!(line.trim(), "RECEIVED");
        let waiting = socket.clone();
        let (done, completed) = mpsc::channel();
        let receive = thread::spawn(move || {
            let _ = done.send(waiting.receive());
        });
        assert!(matches!(
            completed.recv_timeout(Duration::from_millis(5200)),
            Err(mpsc::RecvTimeoutError::Timeout)
        ));
        socket.close();
        assert!(
            completed
                .recv_timeout(Duration::from_secs(2))
                .unwrap()
                .is_err()
        );
        receive.join().unwrap();
    }
}
