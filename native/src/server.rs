use crate::{
    api,
    endpoint::Endpoint,
    protocol::{self, Request, State},
    websocket::Socket,
};
use serde::Serialize;
use serde_json::json;
use std::{
    collections::HashMap,
    sync::{
        Arc, LazyLock, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
        mpsc::{self, SyncSender},
    },
    thread,
    time::Duration,
};

pub struct Client {
    pub id: u64,
    connected: AtomicBool,
    sender: SyncSender<Vec<u8>>,
    socket: Socket,
}
impl Client {
    pub fn alive(&self) -> bool {
        self.connected.load(Ordering::Acquire)
    }
    pub fn close(&self) {
        self.connected.store(false, Ordering::Release);
        self.socket.close();
    }
    pub fn send(&self, value: &impl Serialize) {
        match protocol::encode(value) {
            Ok(bytes) => {
                if self.sender.try_send(bytes).is_err() {
                    self.close();
                }
            }
            Err(error) => {
                api::log(&error);
                self.close();
            }
        }
    }
}

struct Hub {
    clients: HashMap<u64, Arc<Client>>,
    state: State,
}
static HUB: LazyLock<Mutex<Hub>> = LazyLock::new(|| {
    Mutex::new(Hub {
        clients: HashMap::new(),
        state: State::inactive(),
    })
});
static NEXT_ID: AtomicU64 = AtomicU64::new(1);

pub fn publish(state: State) {
    let mut hub = HUB.lock().unwrap();
    hub.state = state;
    for client in hub.clients.values() {
        client.send(&hub.state);
    }
    hub.clients.retain(|_, c| c.alive());
}

pub fn register(client: Arc<Client>, mut snapshot: State) {
    if !client.alive() {
        return;
    }
    snapshot.selection_event = false;
    let mut hub = HUB.lock().unwrap();
    client.send(&json!({"kind":"hello","protocol":"skill-tips-bridge","version":2,"profile":protocol::PROFILE}));
    client.send(&snapshot);
    hub.clients.insert(client.id, client);
}

pub fn remove(id: u64) {
    HUB.lock().unwrap().clients.remove(&id);
}

pub fn start() -> Result<(), String> {
    thread::Builder::new()
        .name("skill-tips-connection".into())
        .spawn(move || {
            let mut previous_error = String::new();
            loop {
                let result = api::get()
                    .base_dir()
                    .and_then(|path| Endpoint::load(&path))
                    .and_then(|endpoint| Socket::connect(&endpoint));
                match result {
                    Ok(socket) => {
                        previous_error.clear();
                        api::log("已连接 URA 技能桥接 WebSocket");
                        if let Err(error) = communicate(socket) {
                            api::log(&error);
                        }
                    }
                    Err(error) if error != previous_error => {
                        api::log(&format!("连接 URA 技能桥接失败: {error}"));
                        previous_error = error;
                    }
                    Err(_) => {}
                }
                thread::sleep(Duration::from_secs(1));
            }
        })
        .map_err(|error| format!("创建 WebSocket 连接线程: {error}"))?;
    Ok(())
}

fn communicate(socket: Socket) -> Result<(), String> {
    let (sender, receiver) = mpsc::sync_channel::<Vec<u8>>(128);
    let client = Arc::new(Client {
        id: NEXT_ID.fetch_add(1, Ordering::Relaxed),
        connected: AtomicBool::new(true),
        sender,
        socket,
    });
    let output_client = client.clone();
    let output = thread::Builder::new()
        .name("skill-tips-output".into())
        .spawn(move || {
            while output_client.alive() {
                match receiver.recv_timeout(Duration::from_millis(250)) {
                    Ok(bytes) => {
                        if let Err(error) = output_client.socket.send(&bytes) {
                            if output_client.alive() {
                                api::log(&error);
                            }
                            break;
                        }
                    }
                    Err(mpsc::RecvTimeoutError::Timeout) => {}
                    Err(mpsc::RecvTimeoutError::Disconnected) => break,
                }
            }
            output_client.close();
        })
        .map_err(|error| format!("创建 WebSocket 发送线程: {error}"))?;
    let result = (|| {
        crate::enqueue(crate::Task::Register(client.clone()))?;
        while client.alive() {
            let message = match client.socket.receive() {
                Ok(Some(message)) => message,
                Ok(None) => break,
                Err(error) if !client.alive() => {
                    let _ = error;
                    break;
                }
                Err(error) => return Err(format!("连接 {}: {error}", client.id)),
            };
            let request = serde_json::from_slice::<Request>(&message)
                .map_err(|error| format!("连接 {} 无效请求 JSON: {error}", client.id))?;
            crate::enqueue(crate::Task::Request(client.clone(), request))?;
        }
        Ok(())
    })();
    client.close();
    remove(client.id);
    let _ = crate::enqueue(crate::Task::Disconnected(client.id));
    output.join().map_err(|_| "WebSocket 发送线程发生 panic")?;
    result
}
