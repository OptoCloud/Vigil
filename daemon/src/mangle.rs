//! Test input that is wrong on purpose. Every parser in vigild reads something it does not
//! control (`smartctl`'s and `zpool`'s JSON, `/proc`, an HBA's reply), and the fixtures only
//! show what those look like when they are right (docs/SECURITY.md §4.3). These generate what
//! they look like when they are not: every value swapped for the wrong kind of value, every
//! line dropped or cut short, every number made absurd. The parsers' tests feed them in and
//! require an answer, not a panic.
//!
//! Derived from the fixture and exhaustive, not random: a run that fails fails the same way
//! every time, and the variant that did it is in the assertion message.

use serde_json::Value;

/// What a field may turn out to hold instead of what was expected of it.
fn wrong_values() -> [Value; 8] {
    [
        Value::Null,
        Value::Bool(true),
        Value::from(-1),
        Value::from(u64::MAX),
        Value::from(1e300),
        Value::from("not what was expected"),
        Value::Array(vec![]),
        Value::Object(serde_json::Map::new()),
    ]
}

/// `json` with each of its values, at every depth, replaced in turn by each wrong value, and
/// with each removed. One change per variant.
pub fn json_variants(json: &str) -> Vec<String> {
    let root: Value = serde_json::from_str(json).expect("the fixture is JSON");
    let mut paths = Vec::new();
    collect_paths(&root, &mut Vec::new(), &mut paths);

    let mut variants = Vec::new();
    for path in &paths {
        for wrong in wrong_values() {
            let mut changed = root.clone();
            *at(&mut changed, path) = wrong;
            variants.push(changed.to_string());
        }

        let mut removed = root.clone();
        remove(&mut removed, path);
        variants.push(removed.to_string());
    }

    variants
}

#[derive(Clone)]
enum Step {
    Key(String),
    Index(usize),
}

fn collect_paths(value: &Value, here: &mut Vec<Step>, paths: &mut Vec<Vec<Step>>) {
    if !here.is_empty() {
        paths.push(here.clone());
    }

    match value {
        Value::Object(fields) => {
            for (key, child) in fields {
                here.push(Step::Key(key.clone()));
                collect_paths(child, here, paths);
                here.pop();
            }
        }
        Value::Array(items) => {
            for (index, child) in items.iter().enumerate() {
                here.push(Step::Index(index));
                collect_paths(child, here, paths);
                here.pop();
            }
        }
        _ => {}
    }
}

fn at<'a>(root: &'a mut Value, path: &[Step]) -> &'a mut Value {
    path.iter().fold(root, |value, step| match step {
        Step::Key(key) => &mut value[key.as_str()],
        Step::Index(index) => &mut value[*index],
    })
}

fn remove(root: &mut Value, path: &[Step]) {
    let (last, parent) = path.split_last().expect("a path has at least one step");
    match (at(root, parent), last) {
        (Value::Object(fields), Step::Key(key)) => {
            fields.remove(key);
        }
        (Value::Array(items), Step::Index(index)) => {
            items.remove(*index);
        }
        _ => unreachable!("a path leads through objects by key and arrays by index"),
    }
}

/// `text` cut off after each character, with each line dropped, and with each number in it
/// replaced by something that is not one, is negative, or does not fit.
pub fn text_variants(text: &str) -> Vec<String> {
    let mut variants: Vec<String> = text.char_indices().map(|(index, _)| text[..index].to_owned()).collect();

    let lines: Vec<&str> = text.lines().collect();
    for skipped in 0..lines.len() {
        let kept: Vec<&str> = lines.iter().enumerate().filter(|(index, _)| *index != skipped).map(|(_, line)| *line).collect();
        variants.push(kept.join("\n"));
    }

    let tokens: Vec<&str> = text.split_inclusive(char::is_whitespace).collect();
    for (changed, token) in tokens.iter().enumerate() {
        if !token.trim().bytes().any(|byte| byte.is_ascii_digit()) {
            continue;
        }

        for wrong in ["x", "-1", "99999999999999999999999999", "1e999", "", "\u{0}"] {
            let rebuilt: String = tokens
                .iter()
                .enumerate()
                .map(|(index, original)| {
                    if index == changed {
                        // The whitespace the token ended with stays, so only the value changes.
                        format!("{wrong}{}", &original[original.trim_end().len()..])
                    } else {
                        (*original).to_owned()
                    }
                })
                .collect();
            variants.push(rebuilt);
        }
    }

    variants
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn json_variants_change_one_value_at_a_time_at_every_depth() {
        let variants = json_variants(r#"{ "a": { "b": [1, 2] } }"#);

        // Four values (a, a.b, and its two items), each replaced eight ways and removed once.
        assert_eq!(variants.len(), 4 * 9);
        assert!(variants.contains(&r#"{"a":{"b":[null,2]}}"#.to_owned()));
        assert!(variants.contains(&r#"{"a":{"b":[2]}}"#.to_owned()));
        assert!(variants.contains(&r#"{"a":"not what was expected"}"#.to_owned()));
        assert!(variants.contains(&"{}".to_owned()));
    }

    #[test]
    fn text_variants_truncate_drop_lines_and_spoil_numbers() {
        let variants = text_variants("a 1\nb 2\n");

        assert!(variants.contains(&"a 1\nb".to_owned()));
        assert!(variants.contains(&"b 2".to_owned()));
        assert!(variants.contains(&"a x\nb 2\n".to_owned()));
        assert!(variants.contains(&"a 1\nb 99999999999999999999999999\n".to_owned()));
    }
}
