using YamlDotNet.Serialization;

namespace RnFlashTool.App.Models;

/// <summary>
/// 一个受支持芯片的定义，对应 <c>chips/</c> 目录下的一个 YAML 文件。
/// 新增芯片只需往该目录里放一个新的 .yaml 文件，无需改代码。
/// </summary>
public sealed class ChipDefinition
{
    public const string DefaultTransport = "swd";

    /// <summary>唯一标识，小写，例如 “stm32f103”。同名时用户目录下的定义覆盖内置定义。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>界面上显示的名称，例如 “STM32F103”。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>所属系列，用于下拉列表分组，例如 “STM32F1”。</summary>
    public string Series { get; set; } = string.Empty;

    /// <summary>OpenOCD target 配置文件，例如 “target/stm32f1x.cfg”。</summary>
    public string TargetConfig { get; set; } = string.Empty;

    /// <summary>
    /// 传输方式：Cortex-M 一般是 swd，ESP32 这类只能走 jtag。
    /// 留空时按 swd 处理，所以已有的 STM32 配置不受影响。
    /// </summary>
    public string Transport { get; set; } = DefaultTransport;

    /// <summary>手动输入型号时用于匹配的前缀；留空则退回使用 <see cref="Id"/>。</summary>
    public List<string> MatchPrefixes { get; set; } = [];

    /// <summary>默认烧录起始地址。</summary>
    public string FlashAddress { get; set; } = FlashOptions.DefaultAddress;

    /// <summary>可选：Flash 容量，设置后“读取固件”会用它作为默认读取大小。</summary>
    public string? FlashSize { get; set; }

    /// <summary>可选：调试器速度（kHz），设置后覆盖编程器接口的默认速度。</summary>
    public int? AdapterSpeed { get; set; }

    /// <summary>可选：额外追加到 OpenOCD 配置里的命令行，用于特殊芯片的定制。</summary>
    public List<string> ExtraConfigLines { get; set; } = [];

    public string Description { get; set; } = string.Empty;

    /// <summary>该定义来自哪个文件，运行期填充，不写入 YAML。</summary>
    [YamlIgnore]
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>定义是否来自用户目录（可覆盖内置定义）。</summary>
    [YamlIgnore]
    public bool IsUserDefined { get; set; }

    /// <summary>下拉列表里显示的文本。</summary>
    [YamlIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>实际参与前缀匹配的列表，按长度降序，保证更精确的型号优先命中。</summary>
    public IEnumerable<string> EffectivePrefixes =>
        (MatchPrefixes.Count > 0 ? MatchPrefixes : [Id])
        .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
        .Select(prefix => prefix.Trim())
        .OrderByDescending(prefix => prefix.Length);

    /// <summary>校验必填字段，返回错误说明；通过校验时返回 <c>null</c>。</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            return "缺少 id 字段";
        }

        if (string.IsNullOrWhiteSpace(TargetConfig))
        {
            return "缺少 targetConfig 字段";
        }

        if (string.IsNullOrWhiteSpace(Transport))
        {
            return "transport 不能为空，留空请直接省略该字段";
        }

        return null;
    }

    public override string ToString() => DisplayName;
}
