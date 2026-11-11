$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /out:"$here\AudioSwitch.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "$here\AudioSwitch.cs"
if ($LASTEXITCODE -eq 0) { "Built $here\AudioSwitch.exe" } else { exit $LASTEXITCODE }
