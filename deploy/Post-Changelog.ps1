# Posts a changelog entry to the Wind-Up Key Discord changelog channel through its webhook. The entry is a Markdown file
# in Discord's format; each block (separated by a blank line) is its own post, at most Discord's 2,000 characters, so each
# release is a message of its own. Nothing pings anyone (no mentions are allowed) and links don't unfurl.
#
# The webhook is a secret: it lives in a file outside the repository (by default ~\.secrets\windupkey-changelog-webhook.txt,
# one line, https://discord.com/api/webhooks/<number>/<token>) and is never printed. Drafts go in private\changelog\,
# which .gitignore keeps out of this public repo.
#
#   powershell -File deploy\Post-Changelog.ps1 -File private\changelog\0.2.6.0.md -Preview    # shows the posts, sends nothing
#   powershell -File deploy\Post-Changelog.ps1 -File private\changelog\0.2.6.0.md            # posts them
param(
    [Parameter(Mandatory = $true)]
    [string]$File,
    [string]$WebhookFile = (Join-Path $HOME '.secrets\windupkey-changelog-webhook.txt'),
    [switch]$Preview
)
$ErrorActionPreference = 'Stop'
$limit = 2000

$text = [IO.File]::ReadAllText((Resolve-Path $File), [Text.Encoding]::UTF8).Replace("`r`n", "`n").Trim()
if (-not $text) {
    throw "$File is empty."
}

# One post per block (separated by blank lines): each release its own message.
$posts = New-Object System.Collections.Generic.List[string]
foreach ($block in ($text -split "`n\s*`n")) {
    $block = $block.Trim()
    if ($block.Length -gt $limit) {
        throw "One block of $File is $($block.Length) characters, over Discord's ${limit}: shorten it."
    }

    if ($block) {
        $posts.Add($block)
    }
}

if ($Preview) {
    for ($i = 0; $i -lt $posts.Count; $i++) {
        Write-Output "----- post $($i + 1) of $($posts.Count), $($posts[$i].Length) characters"
        Write-Output $posts[$i]
    }

    return
}

if (-not (Test-Path $WebhookFile)) {
    throw "No webhook file at $WebhookFile (one line: the changelog channel's webhook URL)."
}

$webhook = (Get-Content $WebhookFile -TotalCount 1).Trim()
if ($webhook -notmatch '^https://discord\.com/api/webhooks/\d+/[A-Za-z0-9_-]+$') {
    throw "The webhook file doesn't hold a Discord webhook URL (https://discord.com/api/webhooks/<number>/<token>)."
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
for ($i = 0; $i -lt $posts.Count; $i++) {
    # flags 4: links don't unfurl. allowed_mentions with nothing: no one is pinged, whatever the text says.
    $body = @{ content = $posts[$i]; allowed_mentions = @{ parse = @() }; flags = 4 } | ConvertTo-Json -Depth 4 -Compress
    try {
        Invoke-RestMethod -Method Post -Uri "$webhook`?wait=true" -ContentType 'application/json; charset=utf-8' `
            -Body ([Text.Encoding]::UTF8.GetBytes($body)) | Out-Null
    } catch {
        $status = $_.Exception.Response.StatusCode.value__
        throw "Discord refused post $($i + 1) of $($posts.Count) (HTTP $status). Posts before it went out."
    }

    Write-Output "Posted $($i + 1) of $($posts.Count) ($($posts[$i].Length) characters)."
    if ($i -lt $posts.Count - 1) {
        Start-Sleep -Milliseconds 1500
    }
}
