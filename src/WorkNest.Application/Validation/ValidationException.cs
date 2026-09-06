namespace WorkNest.Application.Validation;

/// <summary>输入校验失败；界面捕获后以窗口内提示展示（普通错误不弹窗）。</summary>
public sealed class ValidationException : Exception
{
    public ValidationException(string message) : base(message)
    {
    }
}
