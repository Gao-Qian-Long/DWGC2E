using System.Security.Cryptography;
using System.Text;

namespace LicenseGenerator;

/// <summary>
/// Command-line tool for generating DWG Translator license activation codes.
/// Usage:
///   dotnet run -- perpetual <machine-id>
///   dotnet run -- subscription <machine-id> <months>
/// </summary>
class Program
{
    private const string SecretKey = "DWG-Translator-2026-Secret-Key-v1";

    static void Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("  DWG Translator 激活码生成工具");
        Console.WriteLine("========================================");
        Console.WriteLine();

        if (args.Length == 0)
        {
            ShowInteractiveMenu();
            return;
        }

        string mode = args[0].ToLowerInvariant();
        switch (mode)
        {
            case "perpetual" when args.Length >= 2:
                GeneratePerpetual(args[1]);
                break;
            case "subscription" when args.Length >= 3 && int.TryParse(args[2], out var months):
                GenerateSubscription(args[1], months);
                break;
            default:
                Console.WriteLine("用法:");
                Console.WriteLine("  dotnet run -- perpetual <machine-id>");
                Console.WriteLine("  dotnet run -- subscription <machine-id> <months>");
                break;
        }
    }

    static void ShowInteractiveMenu()
    {
        while (true)
        {
            Console.WriteLine("请选择操作:");
        Console.WriteLine("  1. 生成永久授权码 (买断制 ¥699)");
        Console.WriteLine("  2. 生成订阅授权码 (月费 ¥49 / 年费 ¥399)");
        Console.WriteLine("  3. 验证激活码");
        Console.WriteLine("  4. 退出");
            Console.Write("> ");

            var choice = Console.ReadLine()?.Trim();
            Console.WriteLine();

            switch (choice)
            {
                case "1":
                    Console.Write("请输入机器标识 (Machine ID): ");
                    var pmId = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(pmId))
                        GeneratePerpetual(pmId);
                    break;
                case "2":
                    Console.Write("请输入机器标识 (Machine ID): ");
                    var smId = Console.ReadLine()?.Trim();
                    Console.Write("请输入订阅月数 (1=月费, 12=年费): ");
                    if (!string.IsNullOrEmpty(smId) && int.TryParse(Console.ReadLine()?.Trim(), out var months))
                        GenerateSubscription(smId, months);
                    break;
                case "3":
                    Console.WriteLine("批量生成功能待实现。请逐个生成。");
                    break;
                case "4":
                    return;
                default:
                    Console.WriteLine("无效选择，请重试。");
                    break;
            }

            Console.WriteLine();
        }
    }

    static void GeneratePerpetual(string machineId)
    {
        var payload = $"{machineId}|{ComputeChecksum(machineId)}";
        var code = $"DwgTranslator-P-{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))}";

        Console.WriteLine("永久授权码已生成:");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(code);
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"绑定机器: {machineId}");
        Console.WriteLine("有效期: 永久");
        Console.WriteLine();
    }

    static void GenerateSubscription(string machineId, int months)
    {
        var expiry = DateTime.UtcNow.AddMonths(months);
        var payload = $"{machineId}|{expiry.Ticks}|{ComputeChecksum(machineId + expiry.Ticks)}";
        var code = $"DwgTranslator-S-{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))}";

        Console.WriteLine("订阅授权码已生成:");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(code);
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"绑定机器: {machineId}");
        Console.WriteLine($"有效期至: {expiry:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"订阅时长: {months} 个月");
        Console.WriteLine();
    }

    static string ComputeChecksum(string data)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(data + SecretKey));
        return Convert.ToHexString(bytes)[..8];
    }
}
