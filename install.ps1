# Installs the latest DiffDino release on Windows.
#
#   irm https://raw.githubusercontent.com/Zeejfps/GitBench/main/install.ps1 | iex
#
# Downloads the installer to a temp folder and runs it. The installer puts DiffDino in your
# user profile (no admin needed) and starts it when done.
#
# The body is one script block invoked on the last line, so its variables don't leak into
# the caller's session and a download cut off midway runs nothing.

& {
    $ErrorActionPreference = 'Stop'
    # Windows PowerShell 5.1 renders the progress bar so slowly it throttles the download.
    $ProgressPreference = 'SilentlyContinue'
    # Windows PowerShell 5.1 may not offer TLS 1.2 by default, which GitHub requires.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $url = 'https://github.com/Zeejfps/GitBench/releases/latest/download/DiffDino-win-x64-Setup.exe'
    $setup = Join-Path ([IO.Path]::GetTempPath()) 'DiffDino-Setup.exe'

    try {
        Write-Host 'Downloading DiffDino for Windows...'
        Invoke-WebRequest -Uri $url -OutFile $setup -UseBasicParsing

        Write-Host 'Installing...'
        $process = Start-Process -FilePath $setup -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "The installer exited with code $($process.ExitCode)."
        }
        Write-Host 'DiffDino is installed.'
    }
    finally {
        Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
    }
}
