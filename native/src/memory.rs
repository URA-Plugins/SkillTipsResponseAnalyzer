use windows::Win32::System::{Diagnostics::Debug::ReadProcessMemory, Threading::GetCurrentProcess};

pub fn bytes(address: usize, length: usize) -> Result<Vec<u8>, String> {
    if address == 0 {
        return Err("读取空 IL2CPP 地址".into());
    }
    let mut result = vec![0; length];
    let mut read = 0;
    unsafe {
        ReadProcessMemory(
            GetCurrentProcess(),
            address as _,
            result.as_mut_ptr().cast(),
            length,
            Some(&mut read),
        )
    }
    .map_err(|e| format!("读取 0x{address:X}, {length} 字节: {e}"))?;
    if read != length {
        return Err(format!("读取 0x{address:X}: 预期 {length}，实际 {read}"));
    }
    Ok(result)
}
pub fn pointer(object: usize, offset: usize) -> Result<usize, String> {
    Ok(usize::from_le_bytes(
        bytes(object.checked_add(offset).ok_or("地址溢出")?, 8)?
            .try_into()
            .unwrap(),
    ))
}
pub fn int(object: usize, offset: usize) -> Result<i32, String> {
    Ok(i32::from_le_bytes(
        bytes(object.checked_add(offset).ok_or("地址溢出")?, 4)?
            .try_into()
            .unwrap(),
    ))
}
pub fn boolean(object: usize, offset: usize) -> Result<bool, String> {
    Ok(bytes(object.checked_add(offset).ok_or("地址溢出")?, 1)?[0] != 0)
}
pub fn string(object: usize) -> Result<String, String> {
    let length = int(object, 0x10)?;
    if !(0..=128).contains(&length) {
        return Err(format!("身份字符串长度无效: {length}"));
    }
    let data = bytes(object + 0x14, length as usize * 2)?;
    String::from_utf16(
        &data
            .chunks_exact(2)
            .map(|c| u16::from_le_bytes([c[0], c[1]]))
            .collect::<Vec<_>>(),
    )
    .map_err(|e| format!("身份字符串 UTF-16 无效: {e}"))
}
pub fn list(object: usize) -> Result<Vec<usize>, String> {
    let count = int(object, 0x18)?;
    if !(0..=4096).contains(&count) {
        return Err(format!("List 0x{object:X} 长度无效: {count}"));
    }
    if count == 0 {
        return Ok(vec![]);
    }
    let items = pointer(object, 0x10)?;
    if count as usize > pointer(items, 0x18)? {
        return Err("List 长度超过数组容量".into());
    }
    (0..count as usize)
        .map(|i| {
            let item = pointer(items, 0x20 + i * 8)?;
            if item == 0 {
                Err(format!("List 第 {i} 项为空"))
            } else {
                Ok(item)
            }
        })
        .collect()
}
pub fn ids(set: usize) -> Result<Vec<i32>, String> {
    let count = int(set, 0x20)?;
    let last = int(set, 0x24)?;
    if count < 0 || last < count || last > 4096 {
        return Err(format!("HashSet 长度无效: count={count}, last={last}"));
    }
    if last == 0 {
        return Ok(vec![]);
    }
    let slots = pointer(set, 0x18)?;
    if last as usize > pointer(slots, 0x18)? {
        return Err("HashSet 长度超过数组容量".into());
    }
    let mut result = vec![];
    for i in 0..last as usize {
        let slot = slots + 0x20 + i * 12;
        if int(slot, 0)? >= 0 {
            result.push(int(slot, 8)?);
        }
    }
    if result.len() != count as usize {
        return Err("HashSet 元素计数不一致".into());
    }
    result.sort_unstable();
    if result.iter().any(|id| *id <= 0) || result.windows(2).any(|pair| pair[0] == pair[1]) {
        return Err("HashSet 包含非正或重复技能 ID".into());
    }
    Ok(result)
}
