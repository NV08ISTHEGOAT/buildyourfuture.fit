# Copyright (c) 2026 Build Your Future. All rights reserved.
# Published only so anyone can check that it is safe. This is NOT open source: no license is granted
# to copy, modify, reuse or redistribute it. Terms: https://buildyourfuture.fit/eula.html
# Builds "Build Your Future Installer.exe" with the C# compiler that comes with Windows (.NET Framework 4).
param([string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Build Your Future Installer.exe'))

$src = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$wpf = Join-Path $fw 'WPF'
$ico = Join-Path (Split-Path -Parent $src) 'installer.ico'

$refs = @(
    (Join-Path $wpf 'PresentationFramework.dll'), (Join-Path $wpf 'PresentationCore.dll'), (Join-Path $wpf 'WindowsBase.dll'),
    'System.Xaml.dll', 'System.Xml.dll', 'System.Core.dll', 'System.Management.dll', 'System.Windows.Forms.dll',
    'System.Drawing.dll', 'System.Web.Extensions.dll'
) | ForEach-Object { "/reference:`"$_`"" }

$cscArgs = @('/nologo', '/target:winexe', '/optimize+', "/out:`"$Out`"", "/win32icon:`"$ico`"",
             "/resource:`"$(Join-Path $src 'window.xaml')`",window.xaml") + $refs + @("`"$(Join-Path $src 'Installer.cs')`"")
& $csc @cscArgs
exit $LASTEXITCODE
