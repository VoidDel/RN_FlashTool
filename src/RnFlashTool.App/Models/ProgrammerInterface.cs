namespace RnFlashTool.App.Models;

/// <summary>
/// 一种调试器/编程器接口，以及它对应的 OpenOCD 配置片段。
/// 传输方式（swd / jtag）不写在这里，而是由芯片定义决定：
/// 同一个调试器接 STM32 走 SWD、接 ESP32 走 JTAG 是常态。
/// </summary>
/// <param name="Id">配置里使用的标识。</param>
/// <param name="DisplayName">界面上显示的名称。</param>
/// <param name="ConfigLines">加载该接口所需的 OpenOCD 命令。</param>
/// <param name="ResetConfig">该接口默认的复位方式。</param>
/// <param name="DefaultSpeed">默认调试速度（kHz），0 表示不显式设置。</param>
/// <param name="SupportedTransports">支持的传输方式，用于在选错时给出提示。</param>
public sealed record ProgrammerInterface(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ConfigLines,
    string ResetConfig,
    int DefaultSpeed = 0,
    IReadOnlyList<string>? SupportedTransports = null)
{
    public const string DefaultResetConfig = "reset_config srst_only";

    public static readonly ProgrammerInterface StLink = new(
        "stlink",
        "ST-Link",
        ["source [find interface/stlink.cfg]"],
        "reset_config srst_only srst_nogate connect_assert_srst",
        DefaultSpeed: 4000,
        SupportedTransports: ["swd", "hla_swd", "jtag"]);

    public static readonly ProgrammerInterface JLink = new(
        "jlink",
        "J-Link",
        ["source [find interface/jlink.cfg]"],
        DefaultResetConfig,
        SupportedTransports: ["swd", "jtag"]);

    public static readonly ProgrammerInterface CmsisDap = new(
        "cmsis-dap",
        "CMSIS-DAP",
        ["source [find interface/cmsis-dap.cfg]"],
        DefaultResetConfig,
        SupportedTransports: ["swd", "jtag"]);

    public static readonly ProgrammerInterface EspUsbJtag = new(
        "esp_usb_jtag",
        "ESP USB-JTAG（内置）",
        ["source [find interface/esp_usb_jtag.cfg]"],
        DefaultResetConfig,
        SupportedTransports: ["jtag"]);

    public static readonly ProgrammerInterface FtdiEspProg = new(
        "esp_prog",
        "ESP-PROG（FTDI）",
        ["source [find interface/ftdi/esp32_devkitj_v1.cfg]"],
        DefaultResetConfig,
        SupportedTransports: ["jtag"]);

    public static IReadOnlyList<ProgrammerInterface> All { get; } =
        [StLink, JLink, CmsisDap, EspUsbJtag, FtdiEspProg];

    public static ProgrammerInterface FromId(string? id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ?? CmsisDap;

    /// <summary>该接口是否支持指定的传输方式；未声明支持列表时一律放行。</summary>
    public bool Supports(string transport) =>
        SupportedTransports is null ||
        SupportedTransports.Contains(transport, StringComparer.OrdinalIgnoreCase);

    public override string ToString() => DisplayName;
}
