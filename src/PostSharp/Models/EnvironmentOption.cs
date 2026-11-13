namespace PostSharp.Models;

/// <summary>
/// 环境下拉框的展示项。Arc 的 <c>Select</c> 只在选中项变化时读取一次文本，
/// 直接用 <see cref="EnvironmentModel"/> 时改名不会刷新，因此用不可变投影承载名称。
/// </summary>
public sealed record EnvironmentOption(Guid Id, string Name)
{
    public override string ToString() => Name;
}
