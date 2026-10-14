using System.Windows.Input;

namespace WowsBattleIntel.Client.ViewModels;

/// <summary>
/// 轻量命令实现。
/// </summary>
/// <remarks>
/// WPF 的 ICommand 需要手动实现，这里提供一个最小可用版本，
/// 支持异步执行与执行条件判定。
/// </remarks>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly Func<object?, bool>? canExecute;
    private bool isRunning;

    /// <summary>
    /// 构造命令。
    /// </summary>
    /// <param name="execute">执行逻辑，支持异步。</param>
    /// <param name="canExecute">是否可执行。</param>
    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
    {
        // 执行中时禁止重入。
        return !isRunning && (canExecute?.Invoke(parameter) ?? true);
    }

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        isRunning = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute(parameter).ConfigureAwait(true);
        }
        finally
        {
            isRunning = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
