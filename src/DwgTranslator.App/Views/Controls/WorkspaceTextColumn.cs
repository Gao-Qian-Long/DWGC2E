using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DwgTranslator.App.Views.Controls;

/// <summary>
/// Keeps a cell's pending text out of the POCO until the workspace transaction commits.
/// DataGrid explicitly updates TwoWay bindings during CommitEdit, even with an Explicit trigger.
/// An unbound editor prevents that write from bypassing our rollback snapshot.
/// </summary>
public sealed class WorkspaceTextColumn : DataGridTextColumn
{
    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem)
    {
        var editor = (TextBox)base.GenerateEditingElement(cell, dataItem);
        var value = editor.Text;
        BindingOperations.ClearBinding(editor, TextBox.TextProperty);
        editor.Text = value;
        return editor;
    }
}
