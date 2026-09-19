using CommunityToolkit.Mvvm.Input;
using DwgTranslator.Core.Api;
using DwgTranslator.Core.Models;
using DwgTranslator.Core.Services;
using System.Text.Json;

namespace DwgTranslator.App.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task MergeTermsAsync() => await OpenCloudWorkspaceAsync();
}
