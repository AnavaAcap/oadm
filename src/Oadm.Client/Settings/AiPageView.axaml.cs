using Avalonia;
using Avalonia.Controls;

namespace Oadm.Client.Settings;

/// <summary>The rail page MCP &amp; Assistant (see <see cref="AiAgentsViewModel"/>): the card MCP left, the card Assistant right.</summary>
public partial class AiPageView : UserControl
{
    /// <summary>
    /// Below this width of the page the Assistant card moves under the MCP card (small or scaled screens: the access keys
    /// table needs about 720 px).
    /// </summary>
    public const double TwoColumnWidth = 1500;

    private bool? _twoColumns;

    public AiPageView()
    {
        InitializeComponent();
        Columns.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
    }

    /// <summary>Whether the two cards stand side by side now (tests).</summary>
    public bool HasTwoColumns => _twoColumns ?? true;

    private void Arrange(double width)
    {
        bool two = width >= TwoColumnWidth;
        if (two == _twoColumns)
        {
            return;
        }

        _twoColumns = two;
        Columns.ColumnDefinitions[1].Width = new GridLength(two ? 16 : 0);
        Columns.ColumnDefinitions[2].Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(AssistantCard, two ? 2 : 0);
        Grid.SetRow(AssistantCard, two ? 0 : 1);
        AssistantCard.Margin = two ? default : new Thickness(0, 16, 0, 0);
    }
}
