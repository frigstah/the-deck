# Re-takes the fourteen pictures of the deck that the website shows, one for each palette and each
# face. Read tools/shots/README.md before running it: there is one step it cannot do for you.
#
# Everything it needs it starts itself - a server to be on air to, a programme to meter, and a copy
# of Deck that keeps its settings in its own folder. Nothing it does can reach the settings or the
# server list of the Deck you actually broadcast with.

param(
    # Somewhere to put the throwaway copy of Deck and its settings. Not the install, and not %APPDATA%.
    [string]$Work = (Join-Path $env:TEMP "deck-shots"),

    # Where the website keeps them.
    [string]$Out = (Join-Path $PSScriptRoot "..\..\site\shots\palettes"),

    # How long each copy is left running before it is photographed. It has to be long enough to be on
    # air, to have sent something, and to have asked the server how many people are listening - all
    # three are on screen, and the listener count is the slow one.
    [int]$Settle = 50,

    [int]$Port = 8765
)

$ErrorActionPreference = "Stop"

$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$app = Join-Path $Work "app"
$data = Join-Path $app "data"
$exe = Join-Path $app "Deck.exe"

$palettes = "Deck", "Rose", "Graphite", "Arcade", "Dragon", "Forest", "Tide"
$faces = "Dark", "Light"

function Stop-Deck {
    Get-Process -Name Deck -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $exe } | Stop-Process -Force
}

# ---------------------------------------------------------------------- build

Write-Output "building..."
dotnet build (Join-Path $root "src\Deck.App\Deck.App.csproj") -c Release -v quiet --nologo
dotnet build (Join-Path $root "tools\fakecast\fakecast.csproj") -c Release -v quiet --nologo
dotnet build (Join-Path $PSScriptRoot "player\player.csproj") -c Release -v quiet --nologo

$built = Join-Path $root "src\Deck.App\bin\Release\net8.0-windows10.0.19041.0"
$fakecast = (Get-ChildItem (Join-Path $root "tools\fakecast\bin\Release") -Recurse -Filter fakecast.exe | Select-Object -First 1).FullName
$player = (Get-ChildItem (Join-Path $PSScriptRoot "player\bin\Release") -Recurse -Filter "Karaoke Player.exe" | Select-Object -First 1).FullName

# A copy rather than the install, with the marker that makes Deck keep its files beside itself.
Remove-Item $app -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $data | Out-Null
Copy-Item (Join-Path $built "*") $app -Recurse -Force
Set-Content (Join-Path $app "deck-portable.txt") -Value "Photographed by tools/shots/capture.ps1."

# ------------------------------------------------------------------ the sound

$bed = Join-Path $PSScriptRoot "bed.wav"
if (-not (Test-Path $bed)) {
    Write-Output "making the bed..."
    Push-Location $PSScriptRoot
    python bed.py
    Pop-Location
}

# A render endpoint that goes nowhere. Anything played here is captured by the deck and heard by
# nobody, which is the whole reason these can be taken at four in the morning.
$silent = "Steam Streaming Speakers"

# ----------------------------------------------------------------- the server

Write-Output "starting the server and the programme..."
$server = Start-Process $fakecast -ArgumentList $Port, 12, $data -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 2

# fakecast writes the server list itself, so that the password is protected the way the product
# protects it. Only the names are changed here, and they are what the deck puts on screen.
$list = Join-Path $data "servers.json"
$text = (Get-Content $list -Raw).
    Replace('"Name": "loopback"', '"Name": "Karaoke Night"').
    Replace('"StationName": "Loopback Test"', '"StationName": "Karaoke Night"').
    Replace('"BitrateKbps": 128', '"BitrateKbps": 192')
Set-Content $list -Value $text -NoNewline

$id = ([regex]'"Id": "([^"]+)"').Match($text).Groups[1].Value

$music = Start-Process $player -ArgumentList $bed, $silent, 60 -PassThru -WorkingDirectory $PSScriptRoot
Start-Sleep -Seconds 3

# --------------------------------------------------------------- the settings

# Only what the pictures depend on. Everything else is whatever the product's own defaults are,
# which is the point: this should look like a deck somebody just set up, not a deck configured for
# a photograph.
$settings = @{
    SetupCompleted      = $true
    InputDeviceKind     = "Process"
    InputDeviceId       = "process:karaoke player"
    SelectedServerId    = $id
    AutoConnectOnStart  = $true
    ManualTitle         = "Open mic - stage 1"
    LanguageCode        = "en"
    SampleRate          = 44100
    CheckForUpdates     = $false
}

New-Item -ItemType Directory -Force -Path $Out | Out-Null

# ---------------------------------------------------------------- photography

try {
    foreach ($palette in $palettes) {
        foreach ($face in $faces) {
            Stop-Deck
            Start-Sleep -Seconds 2

            $settings.Palette = $palette
            $settings.Theme = $face
            $settings | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $data "settings.json")

            # Started fresh for every picture rather than switched between them. A palette applied to
            # a window that is already up is the same picture in all but one respect: if a control
            # ever kept a brush from the palette before it, only a fresh start would show it.
            Start-Process $exe
            Start-Sleep -Seconds $Settle

            $name = "$($palette.ToLower())-$($face.ToLower()).png"
            & (Join-Path $PSScriptRoot "shoot.ps1") -Out (Join-Path $Out $name) -Exe $exe
        }
    }
}
finally {
    Stop-Deck
    if ($music -and -not $music.HasExited) { $music | Stop-Process -Force }
    if ($server -and -not $server.HasExited) { $server | Stop-Process -Force }
}

Write-Output ""
Write-Output "fourteen pictures in $Out"
Write-Output "now run: dotnet run --project tests/Deck.EncoderCheck"
