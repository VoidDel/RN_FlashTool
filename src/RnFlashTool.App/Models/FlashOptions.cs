namespace RnFlashTool.App.Models;

/// <summary>一次 OpenOCD 操作所需的全部参数快照。</summary>
public sealed class FlashOptions
{
    public const string DefaultAddress = "0x08000000";
    public const string DefaultReadSize = "0x10000";
    public const string DefaultChipId = "stm32f4";
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>openocd 可执行文件路径。</summary>
    public required string OpenOcdPath { get; init; }

    /// <summary>目标芯片定义，来自 chips/ 目录下的 YAML 文件。</summary>
    public required ChipDefinition Chip { get; init; }

    /// <summary>OpenOCD 的 scripts 目录；为空时交给 OpenOCD 自行推断。</summary>
    public string? ScriptsDirectory { get; init; }

    public string FirmwarePath { get; init; } = string.Empty;

    public FirmwareFormat Format { get; init; } = FirmwareFormat.Elf;

    public string Address { get; init; } = DefaultAddress;

    public string ReadSize { get; init; } = DefaultReadSize;

    public bool FullErase { get; init; } = true;

    public ProgrammerInterface Interface { get; init; } = ProgrammerInterface.CmsisDap;

    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    public string TargetChipName => Chip.DisplayName;

    /// <summary>本次使用的传输方式，取自芯片定义。</summary>
    public string Transport => string.IsNullOrWhiteSpace(Chip.Transport)
        ? ChipDefinition.DefaultTransport
        : Chip.Transport.Trim();

    /// <summary>
    /// 拼出 OpenOCD 配置文件的公共头部：选择接口与传输方式、加载 target 配置、
    /// 设置复位方式，最后 init + halt 进入可操作状态。
    /// </summary>
    public string BuildConfigHeader()
    {
        var lines = new List<string>(Interface.ConfigLines);

        // 传输方式由芯片决定：同一个调试器接 STM32 走 SWD、接 ESP32 走 JTAG。
        lines.Add($"transport select {Transport}");

        // 芯片定义里的速度优先于接口默认速度，所以放在接口配置之后。
        var speed = Chip.AdapterSpeed ?? (Interface.DefaultSpeed > 0 ? Interface.DefaultSpeed : null);
        if (speed is > 0)
        {
            lines.Add($"adapter speed {speed}");
        }

        lines.Add($"source [find {Chip.TargetConfig}]");
        lines.Add(Interface.ResetConfig);
        lines.AddRange(Chip.ExtraConfigLines.Where(line => !string.IsNullOrWhiteSpace(line)));
        lines.Add("init");
        lines.Add("halt");
        lines.Add(string.Empty);

        return string.Join("\n", lines);
    }
}
