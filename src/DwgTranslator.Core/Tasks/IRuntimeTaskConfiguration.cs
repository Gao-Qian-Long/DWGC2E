using DwgTranslator.Core.Models;
namespace DwgTranslator.Core.Tasks;
/// <summary>Applies saved options only between queue runs, preserving the running task snapshot.</summary>
public interface IRuntimeTaskConfiguration { void ApplyConfiguration(AppConfig config); }
