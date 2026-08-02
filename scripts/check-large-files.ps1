[CmdletBinding()]
param(
    [long] $MaximumBytes = 25MB
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$forbiddenExtensions = @('.gguf', '.ggml', '.bin', '.safetensors')
$violations = [System.Collections.Generic.List[string]]::new()

Push-Location $repositoryRoot
try {
    # Include untracked, non-ignored files as well so the check is useful before the first commit.
    $trackedFiles = & git ls-files --cached --others --exclude-standard
    foreach ($relativePath in $trackedFiles) {
        if ([string]::IsNullOrWhiteSpace($relativePath)) { continue }
        $fullPath = Join-Path $repositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
        $item = Get-Item -LiteralPath $fullPath
        if ($item.Length -gt $MaximumBytes -or $forbiddenExtensions -contains $item.Extension.ToLowerInvariant()) {
            $violations.Add("$relativePath ($($item.Length) bytes)")
        }
    }
}
finally {
    Pop-Location
}

if ($violations.Count -gt 0) {
    throw "Git管理対象に大容量ファイルまたはモデルが含まれています:`n$($violations -join "`n")"
}

Write-Host "Git管理対象のファイルサイズ検査に合格しました（上限 $MaximumBytes bytes）。"
