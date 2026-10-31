using LeadGen.Core.Tests;

// Usage: dotnet run --project LeadGen.Core.Tests -- [stage1|all]
// Runs offline against an in-process AI double (no Python service / network required).
var which = args.FirstOrDefault() ?? "all";

if (which is "stage1" or "all") await Stage1Tests.RunAll();
if (which is "stage2" or "all") await Stage2Tests.RunAll();
if (which is "stage3" or "all") await Stage3Tests.RunAll();
if (which is "stage4" or "all") await Stage4Tests.RunAll();

return T.Summary();
