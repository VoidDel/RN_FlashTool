namespace RnFlashTool.App.Services;

/// <summary>“读取 / 验证”弹窗里填好的地址范围，长度对验证操作无意义时为空串。</summary>
/// <param name="Address">起始地址，原样保留用户写法（<c>0x</c> 前缀或十进制）。</param>
/// <param name="ReadSize">读取长度，只有读取操作会用到。</param>
public sealed record MemoryRangeInput(string Address, string ReadSize);
