use crate::{
    api::{self, GcRoot},
    memory,
    profile::Methods,
    protocol::{LearnedSkill, Request, State, Training},
    server::{self, Client},
};
use serde_json::json;
use std::{
    collections::{BTreeSet, HashMap},
    sync::Arc,
};

#[derive(Clone)]
struct Prepared {
    request_id: i64,
    controller: usize,
    model: usize,
    before: State,
    ordered_targets: Vec<i32>,
    selected: Vec<i32>,
    remaining: i32,
}

struct Info {
    pointer: usize,
    id: i32,
    acquired: bool,
    available: bool,
    locked: bool,
    unresolvable: bool,
}

#[derive(Default)]
struct EntryWatch {
    controller: usize,
    scheduled: bool,
}

impl EntryWatch {
    fn begin(&mut self, controller: usize) -> bool {
        self.controller = controller;
        let schedule = !self.scheduled;
        self.scheduled = true;
        schedule
    }

    fn leave(&mut self, controller: usize) {
        if self.controller == controller {
            self.controller = 0;
        }
    }

    fn next_frame(&mut self, transitioning: bool) -> bool {
        if self.controller != 0 && transitioning {
            return true;
        }
        self.controller = 0;
        self.scheduled = false;
        false
    }
}

pub struct Runtime {
    pub methods: Methods,
    controller: usize,
    model: usize,
    blocked_controller: usize,
    entry: EntryWatch,
    pub state: State,
    prepared: HashMap<u64, Prepared>,
}

impl Runtime {
    pub fn new(methods: Methods) -> Self {
        Self {
            methods,
            controller: 0,
            model: 0,
            blocked_controller: 0,
            entry: EntryWatch::default(),
            state: State::inactive(),
            prepared: HashMap::new(),
        }
    }

    fn current(&self) -> Result<Option<(usize, usize)>, String> {
        let scene = api::get().existing_singleton(self.methods.scene_field);
        if scene == 0 {
            return Ok(None);
        }
        if self.transitioning(scene)? {
            return Ok(None);
        }
        let controller = self.methods.invoke("current_view", scene, &mut [])?;
        if controller == 0
            || controller == self.blocked_controller
            || unsafe { (api::get().object_class)(controller) } != self.methods.controller_class
        {
            return Ok(None);
        }
        let model = memory::pointer(controller, 0x40)?;
        if model == 0 {
            return Err("技能页 controller._skillLearningModel 为空".into());
        }
        Ok(Some((controller, model)))
    }

    fn transitioning(&self, scene: usize) -> Result<bool, String> {
        Ok(
            memory::boolean(self.methods.invoke("transition", scene, &mut [])?, 0x10)?
                || memory::boolean(
                    self.methods.invoke("scene_transition", scene, &mut [])?,
                    0x10,
                )?,
        )
    }

    fn training(&self) -> Result<Training, String> {
        let work = api::get().existing_singleton(self.methods.work_field);
        if work == 0 {
            return Err("WorkDataManager._instance 为空".into());
        }
        let single_mode = memory::pointer(work, 0x60)?;
        let character = memory::pointer(single_mode, 0x58)?;
        let boxed_points = GcRoot::new(self.methods.invoke("points", character, &mut [])?)?;
        let points = self
            .methods
            .invoke("unbox_int", 0, &mut [boxed_points.object()])?;
        let skill_points = memory::int(points, 0x10)?;
        let mut learned_skills = vec![];
        for skill in memory::list(memory::pointer(character, 0x2a8)?)? {
            learned_skills.push(LearnedSkill {
                id: self.methods.int("learned_id", skill)?,
                level: self.methods.int("learned_level", skill)?,
            });
        }
        learned_skills.sort_by_key(|s| s.id);
        if learned_skills.iter().any(|s| s.id <= 0 || s.level <= 0)
            || learned_skills.windows(2).any(|s| s[0].id == s[1].id)
        {
            return Err("当前已学技能列表包含无效/重复 ID 或等级".into());
        }
        Ok(Training {
            chara_id: self.methods.int("chara_id", character)?,
            start_time: memory::string(self.methods.invoke("start_time", character, &mut [])?)?,
            turn: self.methods.int("turn", single_mode)?,
            skill_points,
            learned_skills,
        })
    }

    fn selected(&self, model: usize) -> Result<Vec<i32>, String> {
        memory::ids(memory::pointer(model, 0x10)?)
    }

    fn infos(&self, model: usize) -> Result<Vec<Info>, String> {
        let mut result = vec![];
        let mut ids = BTreeSet::new();
        for group in memory::list(memory::pointer(model, 0x18)?)? {
            for pointer in memory::list(memory::pointer(group, 0x10)?)? {
                let id = memory::int(pointer, 0x10)?;
                if id <= 0 || !ids.insert(id) {
                    return Err(format!("技能模型包含无效/重复 ID {id}"));
                }
                result.push(Info {
                    pointer,
                    id,
                    acquired: memory::boolean(pointer, 0x18)?,
                    available: memory::boolean(pointer, 0x19)?,
                    locked: memory::boolean(pointer, 0x1a)?,
                    unresolvable: memory::boolean(pointer, 0x40)?,
                });
            }
        }
        Ok(result)
    }

    pub fn refresh(&mut self, selection_event: bool, force_revision: bool) {
        let mut next = State::inactive();
        next.page_id = self.state.page_id;
        match self.current() {
            Ok(Some((controller, model))) => {
                if controller != self.controller || model != self.model {
                    next.page_id += 1;
                    self.prepared.clear();
                }
                self.controller = controller;
                self.model = model;
                match (
                    self.training(),
                    self.selected(model),
                    self.methods.int("remaining", model),
                ) {
                    (Ok(training), Ok(selected), Ok(remaining)) => {
                        next.active = true;
                        next.training = Some(training);
                        next.selected_skill_ids = selected;
                        next.remaining_points = remaining;
                    }
                    (a, b, c) => {
                        next.error = Some(format!(
                            "技能页快照失败: {}",
                            a.err().or(b.err()).or(c.err()).unwrap()
                        ))
                    }
                }
            }
            Ok(None) => {
                self.controller = 0;
                self.model = 0;
            }
            Err(error) => {
                self.controller = 0;
                self.model = 0;
                next.error = Some(error);
            }
        }
        let changed = !self.state.same_data(&next) || force_revision;
        next.revision = self.state.revision + u64::from(changed || self.state.revision == 0);
        next.selection_event = selection_event && next.active;
        self.state = next;
        if changed || selection_event {
            server::publish(self.state.clone());
        }
    }

    pub fn entered(&mut self, controller: usize) -> bool {
        self.blocked_controller = 0;
        self.entry.begin(controller)
    }

    pub fn adopt_current_entry(&mut self) -> Result<bool, String> {
        let scene = api::get().existing_singleton(self.methods.scene_field);
        if scene == 0 {
            return Ok(false);
        }
        let controller = self.methods.invoke("current_view", scene, &mut [])?;
        if controller != 0
            && unsafe { (api::get().object_class)(controller) } == self.methods.controller_class
        {
            return Ok(self.entered(controller));
        }
        Ok(false)
    }

    pub fn poll_entry(&mut self) -> bool {
        if self.entry.controller != 0 {
            self.refresh(false, false);
            let scene = api::get().existing_singleton(self.methods.scene_field);
            match if scene == 0 {
                Ok(false)
            } else {
                self.transitioning(scene)
            } {
                Ok(true) => return self.entry.next_frame(true),
                Ok(false) => {}
                Err(error) => api::log(&format!("等待技能页入场失败: {error}")),
            }
        }
        self.entry.next_frame(false)
    }
    pub fn leaving(&mut self, controller: usize) {
        self.entry.leave(controller);
        self.blocked_controller = controller;
        self.refresh(false, true);
    }
    pub fn changed(&mut self, model: usize, newly_selected: bool) {
        match self.current() {
            Ok(Some((_, current_model))) if current_model == model => {
                self.refresh(newly_selected, true)
            }
            Ok(_) => {}
            Err(error) => {
                api::log(&format!("验证技能事件所属页面失败: {error}"));
                self.refresh(false, false);
            }
        }
    }
    pub fn disconnected(&mut self, client: u64) {
        self.prepared.remove(&client);
    }

    pub fn request(&mut self, client: &Arc<Client>, request: Request) {
        let request_id = request.id();
        let stage = if matches!(request, Request::Replace { .. }) {
            "替换失败"
        } else {
            "预检失败"
        };
        let result = request.validate().and_then(|_| match request {
            Request::Prepare {
                request_id,
                page_id,
                revision,
                training,
                skill_ids,
            } => self.prepare(client, request_id, page_id, revision, training, skill_ids),
            Request::Replace {
                request_id,
                prepared_request_id,
            } => self.replace(client, request_id, prepared_request_id),
        });
        if let Err(error) = result {
            self.prepared.remove(&client.id);
            self.refresh(false, false);
            server::publish(self.state.clone());
            client.send(&json!({"kind":"error", "request_id":request_id,"message":format!("{stage}: {error}")}));
        }
    }

    fn prepare(
        &mut self,
        client: &Arc<Client>,
        request_id: i64,
        page_id: u64,
        revision: u64,
        training: Training,
        skill_ids: Vec<i32>,
    ) -> Result<(), String> {
        self.prepared.remove(&client.id);
        self.refresh(false, false);
        verify_state(&self.state, page_id, revision, &training)?;
        let before = self.state.clone();
        let controller = self.controller;
        let model = self.model;
        let real_infos = self.infos(model)?;
        let ordered_targets = checked_targets(&real_infos, &skill_ids)?;
        let temporary = GcRoot::new(unsafe { (api::get().object_new)(self.methods.model_class) })?;
        self.methods
            .invoke("model_ctor", temporary.object(), &mut [])?;
        let infos = self.infos(temporary.object())?;
        checked_targets(&infos, &skill_ids)?;
        for id in &ordered_targets {
            let info = infos
                .iter()
                .find(|info| info.id == *id)
                .ok_or_else(|| format!("临时模型缺少技能 {id}"))?;
            self.methods
                .invoke("select", temporary.object(), &mut [info.pointer])?;
            if self.methods.int("remaining", temporary.object())? < 0 {
                return Err(format!("选择技能 {id} 后点数不足"));
            }
        }
        let selected = self.selected(temporary.object())?;
        for id in &selected {
            if !infos.iter().any(|info| info.id == *id) {
                return Err(format!("临时模型选中未知技能 {id}"));
            }
        }
        let remaining = self.methods.int("remaining", temporary.object())?;
        self.refresh(false, false);
        if !before.same_data(&self.state)
            || self.state.revision != revision
            || controller != self.controller
            || model != self.model
        {
            return Err("预检期间真实页面或育成状态变化，请重新请求".into());
        }
        let extra = before
            .selected_skill_ids
            .iter()
            .copied()
            .filter(|id| selected.binary_search(id).is_err())
            .collect::<Vec<_>>();
        self.prepared.insert(
            client.id,
            Prepared {
                request_id,
                controller,
                model,
                before,
                ordered_targets,
                selected: selected.clone(),
                remaining,
            },
        );
        client.send(&json!({"kind":"prepared","request_id":request_id,"page_id":page_id,"revision":revision,"selected_skill_ids":selected,"extra_skill_ids":extra,"remaining_points":remaining}));
        Ok(())
    }

    fn replace(
        &mut self,
        client: &Arc<Client>,
        request_id: i64,
        prepared_request_id: i64,
    ) -> Result<(), String> {
        let prepared = self
            .prepared
            .remove(&client.id)
            .ok_or("当前连接没有待执行预检")?;
        if prepared.request_id != prepared_request_id {
            return Err("prepared_request_id 不匹配最新预检".into());
        }
        self.refresh(false, false);
        verify_state(
            &self.state,
            prepared.before.page_id,
            prepared.before.revision,
            prepared.before.training.as_ref().unwrap(),
        )?;
        if self.controller != prepared.controller
            || self.model != prepared.model
            || !self.state.same_data(&prepared.before)
        {
            return Err("页面、选择或育成状态已变化，请重新预检".into());
        }
        if !client.alive() {
            return Err("连接已断开，取消未开始的替换".into());
        }
        let infos = self.infos(self.model)?;
        let result = (|| {
            self.methods.invoke("reset", self.model, &mut [])?;
            for id in &prepared.ordered_targets {
                let info = infos
                    .iter()
                    .find(|i| i.id == *id)
                    .ok_or_else(|| format!("执行时模型缺少技能 {id}"))?;
                self.methods
                    .invoke("select", self.model, &mut [info.pointer])?;
            }
            let selected = self.selected(self.model)?;
            let remaining = self.methods.int("remaining", self.model)?;
            if selected != prepared.selected || remaining != prepared.remaining {
                return Err(format!(
                    "实际选择与预检不一致：IDs={selected:?}，剩余点数={remaining}；预期IDs={:?}，点数={}",
                    prepared.selected, prepared.remaining
                ));
            }
            Ok(())
        })();
        let update = self.methods.invoke("update", self.controller, &mut []);
        let added = prepared.selected.iter().any(|id| {
            prepared
                .before
                .selected_skill_ids
                .binary_search(id)
                .is_err()
        });
        self.refresh(added && result.is_ok(), true);
        result.map_err(|error| format!("重置开始后执行失败，真实选择可能已改变: {error}"))?;
        update.map_err(|error| format!("选择已执行，但刷新技能页失败: {error}"))?;
        if !self.state.active
            || self.state.error.is_some()
            || self.state.selected_skill_ids != prepared.selected
            || self.state.remaining_points != prepared.remaining
        {
            return Err(format!(
                "选择已执行，但最终真实快照不一致: {:?}",
                self.state.error
            ));
        }
        client.send(&json!({"kind":"result","request_id":request_id,"selected_skill_ids":self.state.selected_skill_ids,"remaining_points":self.state.remaining_points}));
        Ok(())
    }
}

fn verify_state(
    state: &State,
    page: u64,
    revision: u64,
    training: &Training,
) -> Result<(), String> {
    if !state.active || state.error.is_some() {
        return Err("当前没有可操作的技能学习页面".into());
    }
    if state.page_id != page
        || state.revision != revision
        || state.training.as_ref() != Some(training)
    {
        return Err("页面版本或育成基础状态已变化，请重新预检".into());
    }
    Ok(())
}

fn checked_targets(infos: &[Info], requested: &[i32]) -> Result<Vec<i32>, String> {
    let mut ordered = vec![];
    for id in requested {
        let info = infos
            .iter()
            .find(|info| info.id == *id)
            .ok_or_else(|| format!("模型缺少推荐技能 {id}"))?;
        if info.acquired || info.locked || !info.available || info.unresolvable {
            return Err(format!("技能 {} 已学习、锁定、不可学习或不可解除", info.id));
        }
        ordered.push(info.id);
    }
    Ok(ordered)
}
