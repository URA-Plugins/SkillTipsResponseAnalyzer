mod api;
mod endpoint;
mod engine;
mod hooks;
mod memory;
mod profile;
mod protocol;
mod server;
mod websocket;

use std::{
    cell::RefCell,
    collections::VecDeque,
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{Arc, LazyLock, Mutex},
};

thread_local! { static RUNTIME: RefCell<Option<engine::Runtime>> = const { RefCell::new(None) }; }
static TASKS: LazyLock<Mutex<VecDeque<Task>>> = LazyLock::new(|| Mutex::new(VecDeque::new()));

pub enum Task {
    Register(Arc<server::Client>),
    Request(Arc<server::Client>, protocol::Request),
    Disconnected(u64),
}

pub fn enqueue(task: Task) -> Result<(), String> {
    let mut queue = TASKS.lock().unwrap();
    if queue.len() >= 256 && !matches!(task, Task::Disconnected(_)) {
        return Err("主线程请求队列已满".into());
    }
    queue.push_back(task);
    drop(queue);
    api::get().schedule(dispatch);
    Ok(())
}

pub fn with_runtime(action: impl FnOnce(&mut engine::Runtime)) {
    if catch_unwind(AssertUnwindSafe(|| {
        RUNTIME.with(|cell| {
            // Calls on a temporary model and batched replacement reenter hooks while
            // this borrow is held. Only the outer operation publishes their result.
            if let Ok(mut runtime) = cell.try_borrow_mut() {
                if let Some(runtime) = runtime.as_mut() {
                    action(runtime);
                }
            }
        })
    }))
    .is_err()
    {
        api::log("主线程回调发生 Rust panic");
    }
}

pub unsafe extern "C" fn poll_entry() {
    let mut pending = false;
    with_runtime(|runtime| pending = runtime.poll_entry());
    if pending {
        api::get().schedule(poll_entry);
    }
}

unsafe extern "C" fn dispatch() {
    let task = TASKS.lock().unwrap().pop_front();
    match task {
        Some(Task::Register(client)) if client.alive() => with_runtime(|runtime| {
            runtime.refresh(false, false);
            server::register(client, runtime.state.clone());
        }),
        Some(Task::Request(client, request)) if client.alive() => {
            with_runtime(|runtime| runtime.request(&client, request))
        }
        Some(Task::Disconnected(id)) => with_runtime(|runtime| runtime.disconnected(id)),
        _ => {}
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn hachimi_init_v3(get_api: api::GetApi, version: i32) -> i32 {
    match catch_unwind(AssertUnwindSafe(|| unsafe {
        api::initialize(get_api, version)
    })) {
        Ok(Ok(())) => {
            api::get().schedule(initialize);
            1
        }
        Ok(Err(error)) => {
            api::log(&error);
            0
        }
        Err(_) => {
            api::log("初始化 Hachimi API 时发生 Rust panic");
            0
        }
    }
}

unsafe extern "C" fn initialize() {
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<(), String> {
        let methods = profile::Methods::load()?;
        hooks::install(&methods)?;
        let mut runtime = engine::Runtime::new(methods);
        let watch_entry = runtime.adopt_current_entry()?;
        RUNTIME.with(|cell| *cell.borrow_mut() = Some(runtime));
        if watch_entry {
            api::get().schedule(poll_entry);
        }
        with_runtime(|runtime| runtime.refresh(false, false));
        server::start()?;
        api::log("原生桥接已启动，将连接 URA WebSocket 端点");
        Ok(())
    }));
    match result {
        Ok(Ok(())) => {}
        Ok(Err(error)) => api::log(&format!("原生桥接初始化失败: {error}")),
        Err(_) => api::log("原生桥接初始化发生 Rust panic"),
    }
}
