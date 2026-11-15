using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using SymbolEnum = FluentIcons.Common.Symbol;

namespace AvaStudio.Views.Controls;

/// <summary>
/// 横向空状态面板：大图标 + 标题/说明 + 主次操作按钮。
/// 与 Screenbox 的 ContentUnavailableView 结构一致，供资料库页面复用。
/// </summary>
public partial class EmptyStateView : UserControl
{
    /// <summary>大图标。</summary>
    public static readonly StyledProperty<SymbolEnum> SymbolProperty =
        AvaloniaProperty.Register<EmptyStateView, SymbolEnum>(nameof(Symbol), SymbolEnum.Info);

    /// <summary>标题。</summary>
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<EmptyStateView, string>(nameof(Title), string.Empty);

    /// <summary>说明文本。</summary>
    public static readonly StyledProperty<string> DescriptionProperty =
        AvaloniaProperty.Register<EmptyStateView, string>(nameof(Description), string.Empty);

    /// <summary>主操作文本。</summary>
    public static readonly StyledProperty<string> PrimaryTextProperty =
        AvaloniaProperty.Register<EmptyStateView, string>(nameof(PrimaryText), string.Empty);

    /// <summary>主操作图标；为 null 时不显示图标。</summary>
    public static readonly StyledProperty<SymbolEnum?> PrimaryIconProperty =
        AvaloniaProperty.Register<EmptyStateView, SymbolEnum?>(nameof(PrimaryIcon));

    /// <summary>主操作命令。</summary>
    public static readonly StyledProperty<ICommand?> PrimaryCommandProperty =
        AvaloniaProperty.Register<EmptyStateView, ICommand?>(nameof(PrimaryCommand));

    /// <summary>次操作文本。</summary>
    public static readonly StyledProperty<string> SecondaryTextProperty =
        AvaloniaProperty.Register<EmptyStateView, string>(nameof(SecondaryText), string.Empty);

    /// <summary>次操作图标；为 null 时不显示图标。</summary>
    public static readonly StyledProperty<SymbolEnum?> SecondaryIconProperty =
        AvaloniaProperty.Register<EmptyStateView, SymbolEnum?>(nameof(SecondaryIcon));

    /// <summary>次操作命令。</summary>
    public static readonly StyledProperty<ICommand?> SecondaryCommandProperty =
        AvaloniaProperty.Register<EmptyStateView, ICommand?>(nameof(SecondaryCommand));

    /// <summary>是否显示主操作。</summary>
    public static readonly StyledProperty<bool> ShowPrimaryProperty =
        AvaloniaProperty.Register<EmptyStateView, bool>(nameof(ShowPrimary), true);

    /// <summary>是否显示次操作。</summary>
    public static readonly StyledProperty<bool> ShowSecondaryProperty =
        AvaloniaProperty.Register<EmptyStateView, bool>(nameof(ShowSecondary), false);

    public EmptyStateView()
    {
        InitializeComponent();
    }

    /// <summary>大图标。</summary>
    public SymbolEnum Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    /// <summary>标题。</summary>
    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>说明文本。</summary>
    public string Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>主操作文本。</summary>
    public string PrimaryText
    {
        get => GetValue(PrimaryTextProperty);
        set => SetValue(PrimaryTextProperty, value);
    }

    /// <summary>主操作图标。</summary>
    public SymbolEnum? PrimaryIcon
    {
        get => GetValue(PrimaryIconProperty);
        set => SetValue(PrimaryIconProperty, value);
    }

    /// <summary>主操作命令。</summary>
    public ICommand? PrimaryCommand
    {
        get => GetValue(PrimaryCommandProperty);
        set => SetValue(PrimaryCommandProperty, value);
    }

    /// <summary>次操作文本。</summary>
    public string SecondaryText
    {
        get => GetValue(SecondaryTextProperty);
        set => SetValue(SecondaryTextProperty, value);
    }

    /// <summary>次操作图标。</summary>
    public SymbolEnum? SecondaryIcon
    {
        get => GetValue(SecondaryIconProperty);
        set => SetValue(SecondaryIconProperty, value);
    }

    /// <summary>次操作命令。</summary>
    public ICommand? SecondaryCommand
    {
        get => GetValue(SecondaryCommandProperty);
        set => SetValue(SecondaryCommandProperty, value);
    }

    /// <summary>是否显示主操作。</summary>
    public bool ShowPrimary
    {
        get => GetValue(ShowPrimaryProperty);
        set => SetValue(ShowPrimaryProperty, value);
    }

    /// <summary>是否显示次操作。</summary>
    public bool ShowSecondary
    {
        get => GetValue(ShowSecondaryProperty);
        set => SetValue(ShowSecondaryProperty, value);
    }
}
