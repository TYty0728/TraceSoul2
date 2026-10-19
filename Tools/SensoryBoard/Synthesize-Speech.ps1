$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
$speechRequest = [Console]::In.ReadToEnd() | ConvertFrom-Json
$OutputPath = [string]$speechRequest.outputPath
if (!$OutputPath) { throw 'Missing temporary output path' }
if (!$speechRequest.text -or $speechRequest.text.Length -gt 500) { throw 'Invalid speech text' }
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $chineseVoice = $synth.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -eq 'zh-CN' } | Select-Object -First 1
    if (!$chineseVoice) { throw 'No installed zh-CN system voice' }
    $synth.SelectVoice($chineseVoice.VoiceInfo.Name)
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(48000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $synth.SetOutputToWaveFile($OutputPath, $format)
    $synth.Speak([string]$speechRequest.text)
    @{ voice = $synth.Voice.Name; engine = 'Windows System.Speech' } | ConvertTo-Json -Compress
} finally { $synth.Dispose() }
