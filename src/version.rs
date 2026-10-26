//! 版本号的宽松解析与比较。
//! 能处理 “0.12.0-6”、“v0.12.0-6”、“0.12.0+dev-02228-ge5888bda3” 这几类写法。

use std::cmp::Ordering;
use std::sync::LazyLock;

use regex::Regex;

static PATTERN: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"(\d+)\.(\d+)(?:\.(\d+))?(?:[-.](\d+))?").expect("版本号正则"));

/// 从任意字符串中提取版本号；提取不到时返回 `None`。
pub fn parse(text: &str) -> Option<String> {
    PATTERN.find(text).map(|m| m.as_str().to_string())
}

/// 从 `openocd --version` 的输出中提取版本号，保留 “+dev” 标记供升级判断使用。
pub fn parse_from_version_output(output: &str) -> Option<String> {
    let first_line = output.lines().map(str::trim).find(|line| !line.is_empty())?;
    let version = parse(first_line).or_else(|| parse(output))?;
    Some(if first_line.to_ascii_lowercase().contains("+dev") { format!("{version}+dev") } else { version })
}

/// 比较两个版本号；无法解析的一方视为更旧。
pub fn compare(left: Option<&str>, right: Option<&str>) -> Ordering {
    to_parts(left).cmp(&to_parts(right))
}

pub fn is_newer(candidate: Option<&str>, current: Option<&str>) -> bool {
    compare(candidate, current) == Ordering::Greater
}

/// 判断 `candidate` 是否值得升级。
/// 内置的 xPack 开发快照形如 “0.12.0+dev-02228”，解析不出打包修订号，
/// 直接按修订号比较会一直提示“有新版本”，所以对开发版只比较 主.次.补丁。
pub fn is_upgrade(candidate: Option<&str>, current: Option<&str>) -> bool {
    if !is_dev_build(current) {
        return is_newer(candidate, current);
    }

    to_parts(candidate)[..3] > to_parts(current)[..3]
}

/// xPack 的开发快照带 “+dev” 后缀。
pub fn is_dev_build(version: Option<&str>) -> bool {
    version.is_some_and(|v| v.to_ascii_lowercase().contains("dev"))
}

fn to_parts(version: Option<&str>) -> [i64; 4] {
    let Some(captures) = version.and_then(|v| PATTERN.captures(v)) else {
        return [-1; 4];
    };

    std::array::from_fn(|i| captures.get(i + 1).and_then(|group| group.as_str().parse().ok()).unwrap_or(0))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_common_forms() {
        assert_eq!(parse("v0.12.0-6").as_deref(), Some("0.12.0-6"));
        assert_eq!(parse("xpack-openocd-0.12.0-7").as_deref(), Some("0.12.0-7"));
        assert_eq!(parse("no version"), None);
    }

    #[test]
    fn keeps_dev_marker() {
        let output = "xPack Open On-Chip Debugger 0.12.0+dev-02228-ge5888bda3-dirty (2025-10-04-22:44)\n";
        assert_eq!(parse_from_version_output(output).as_deref(), Some("0.12.0+dev"));
    }

    #[test]
    fn compares_revision() {
        assert!(is_newer(Some("0.12.0-7"), Some("0.12.0-6")));
        assert!(!is_newer(Some("0.12.0-6"), Some("0.12.0-6")));
        assert!(is_newer(Some("0.12.0"), None));
        assert!(is_newer(Some("3.0.0"), Some("2.0.0")));
    }

    #[test]
    fn dev_build_compares_only_three_parts() {
        assert!(!is_upgrade(Some("0.12.0-7"), Some("0.12.0+dev")));
        assert!(is_upgrade(Some("0.13.0-1"), Some("0.12.0+dev")));
    }
}
