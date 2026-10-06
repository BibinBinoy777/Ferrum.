$ErrorActionPreference = 'Stop'
$assistantCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $assistantCompiler)) {
    $assistantCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $assistantCompiler)) { throw 'The Windows .NET Framework C# compiler is required.' }
$assistantTarget = Join-Path $PSScriptRoot 'Ferrum.exe'
$assistantSource = Join-Path $PSScriptRoot 'src\Ferrum.cs'
& $assistantCompiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output "/out:$assistantTarget" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Net.Http.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll "/win32manifest:$(Join-Path $PSScriptRoot 'src\app.manifest')" $assistantSource
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built $assistantTarget"
