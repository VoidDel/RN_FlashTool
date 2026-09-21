namespace Stm32Flash.App.Models;

/// <summary>一种调试器/编程器接口，以及它对应的 OpenOCD 配置片段。</summary>
public sealed record ProgrammerInterface(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ConfigLines,
    string ResetConfig)
{
    public const string DefaultResetConfig = "reset_config srst_only";

    public static readonly ProgrammerInterface StLink = new(
        "stlink",
        "ST-Link",
        ["source [find interface/stlink.cfg]", "transport select swd", "adapter speed 4000"],
        "reset_config srst_only srst_nogate connect_assert_srst");

    public static readonly ProgrammerInterface JLink = new(
        "jlink",
        "J-Link",
        ["source [find interface/jlink.cfg]", "transport select swd"],
        DefaultResetConfig);

    public static readonly ProgrammerInterface CmsisDap = new(
        "cmsis-dap",
        "CMSIS-DAP",
        ["source [find interface/cmsis-dap.cfg]", "transport select swd"],
        DefaultResetConfig);

    public static IReadOnlyList<ProgrammerInterface> All { get; } = [StLink, JLink, CmsisDap];

    public static ProgrammerInterface FromId(string? id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ?? CmsisDap;

    public override string ToString() => DisplayName;
}
