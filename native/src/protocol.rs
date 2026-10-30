use serde::{Deserialize, Serialize};

pub const PROFILE: &str = "umamusume-20261026-il2cpp-x64";
pub const MAX_MESSAGE: usize = 65_536;

#[derive(Debug, Clone, PartialEq, Eq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct LearnedSkill {
    pub id: i32,
    pub level: i32,
}

#[derive(Debug, Clone, PartialEq, Eq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Training {
    pub chara_id: i32,
    pub start_time: String,
    pub turn: i32,
    pub skill_points: i32,
    pub learned_skills: Vec<LearnedSkill>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct State {
    pub kind: &'static str,
    pub active: bool,
    pub page_id: u64,
    pub revision: u64,
    pub selection_event: bool,
    pub training: Option<Training>,
    pub selected_skill_ids: Vec<i32>,
    pub remaining_points: i32,
    pub error: Option<String>,
}

impl State {
    pub fn inactive() -> Self {
        Self {
            kind: "state",
            active: false,
            page_id: 0,
            revision: 0,
            selection_event: false,
            training: None,
            selected_skill_ids: vec![],
            remaining_points: 0,
            error: None,
        }
    }

    pub fn same_data(&self, other: &Self) -> bool {
        self.active == other.active
            && self.page_id == other.page_id
            && self.training == other.training
            && self.selected_skill_ids == other.selected_skill_ids
            && self.remaining_points == other.remaining_points
            && self.error == other.error
    }
}

#[derive(Debug, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum Request {
    Prepare {
        request_id: i64,
        page_id: u64,
        revision: u64,
        training: Training,
        skill_ids: Vec<i32>,
    },
    Replace {
        request_id: i64,
        prepared_request_id: i64,
    },
}

impl Request {
    pub fn id(&self) -> i64 {
        match self {
            Self::Prepare { request_id, .. } | Self::Replace { request_id, .. } => *request_id,
        }
    }

    pub fn validate(&self) -> Result<(), String> {
        if self.id() <= 0 {
            return Err("request_id 必须为正整数".into());
        }
        match self {
            Self::Prepare {
                page_id,
                revision,
                training,
                skill_ids,
                ..
            } => {
                if *page_id == 0 || *revision == 0 {
                    return Err("page_id 与 revision 必须为正整数".into());
                }
                if skill_ids.is_empty()
                    || skill_ids.len() > 4096
                    || skill_ids.iter().any(|id| *id <= 0)
                {
                    return Err("skill_ids 必须包含 1–4096 个正 ID".into());
                }
                let mut ids = skill_ids.clone();
                ids.sort_unstable();
                ids.dedup();
                if ids.len() != skill_ids.len() {
                    return Err("skill_ids 包含重复 ID".into());
                }
                if training.chara_id <= 0
                    || training.start_time.is_empty()
                    || training.start_time.len() > 128
                    || training.turn < 0
                    || training.skill_points < 0
                    || training.learned_skills.len() > 4096
                    || training
                        .learned_skills
                        .iter()
                        .any(|s| s.id <= 0 || s.level <= 0)
                    || training
                        .learned_skills
                        .windows(2)
                        .any(|s| s[0].id >= s[1].id)
                {
                    return Err("training 身份、点数或按 ID 排序的已学技能列表无效".into());
                }
            }
            Self::Replace {
                prepared_request_id,
                ..
            } if *prepared_request_id <= 0 => return Err("prepared_request_id 必须为正整数".into()),
            _ => {}
        }
        Ok(())
    }
}

pub fn encode(value: &impl Serialize) -> Result<Vec<u8>, String> {
    let bytes = serde_json::to_vec(value).map_err(|e| format!("序列化 JSON: {e}"))?;
    if bytes.len() > MAX_MESSAGE {
        return Err(format!("响应超过 {MAX_MESSAGE} 字节"));
    }
    Ok(bytes)
}
