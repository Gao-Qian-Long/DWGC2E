using DwgTranslator.App.Services;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    public async Task HandleImportShortcutAsync()
    {
        if (CurrentPage is not (PageTranslate or PageBatch))
        {
            ToastService.Info("当前页面不导入图纸，请先进入“图纸翻译”或“批量任务”。");
            return;
        }
        if (IsProcessing)
        {
            ToastService.Warning("当前任务正在执行，完成或取消后再导入图纸。");
            return;
        }
        await ImportDwgCommand.ExecuteAsync(null);
    }

    public async Task HandleSaveShortcutAsync()
    {
        switch (CurrentPage)
        {
            case PageSettings:
                if (SettingsSection == 5)
                {
                    ToastService.Info("“关于与帮助”没有需要保存的设置。");
                    return;
                }
                if (SaveSettingsPage()) ToastService.Success("设置已保存。");
                else ToastService.Warning(SettingsFeedback);
                return;

            case PageGlossary:
                if (!CanEditWorkspace)
                {
                    ToastService.Warning("当前术语工作区暂不可编辑，请等待同步完成后重试。");
                    return;
                }
                if (!HasUnsavedTerms)
                {
                    ToastService.Info("术语库没有需要保存的更改。");
                    return;
                }
                if (await SaveTermEditorAsync()) ToastService.Success("术语更改已保存到本机。");
                else ToastService.Warning(TermFeedback);
                return;

            case PageBatch when IsProofreading:
                if (!HasUnsavedProofreading)
                {
                    ToastService.Info("当前校对没有需要保存的更改。");
                    return;
                }
                if (TrySaveProofreading()) ToastService.Success("校对更改已保存。");
                else ToastService.Warning("校对更改保存失败，请检查后重试。");
                return;

            case PageTranslate:
            case PageBatch:
                if (IsProcessing)
                {
                    ToastService.Info("当前任务正在执行，请完成后再导出。");
                    return;
                }
                if (TranslatedCount <= 0)
                {
                    ToastService.Info("当前没有可导出的翻译结果。");
                    return;
                }
                await ExportDwgCommand.ExecuteAsync(null);
                return;

            default:
                ToastService.Info("当前页面没有需要保存的内容。");
                return;
        }
    }

    public async Task HandleRunShortcutAsync()
    {
        if (CurrentPage == PageBatch && IsProofreading)
        {
            ToastService.Info("校对页面不会启动翻译；请返回任务列表后重试。");
            return;
        }
        if (CurrentPage is not (PageTranslate or PageBatch))
        {
            ToastService.Info("当前页面不执行翻译，请先进入“图纸翻译”或“批量任务”。");
            return;
        }
        if (!HasDrawingFiles)
        {
            ToastService.Info("请先导入 DWG 或 DXF 图纸。");
            return;
        }
        if (IsProcessing)
        {
            ToastService.Info("当前任务正在执行，请勿重复启动。");
            return;
        }
        await TranslateCommand.ExecuteAsync(null);
    }
}
