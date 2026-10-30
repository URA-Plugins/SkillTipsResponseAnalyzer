param([Parameter(Mandatory)][int]$Port)
$ErrorActionPreference = 'Stop'
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
$listener.Start()
[Console]::WriteLine('READY')
try {
    $connection = $listener.AcceptTcpClient()
    $stream = $connection.GetStream()
    $headers = [IO.StreamReader]::new($stream, [Text.Encoding]::ASCII, $false, 1024, $true)
    if ($headers.ReadLine() -ne 'GET /SkillTipsResponseAnalyzer/bridge HTTP/1.1') { throw 'Unexpected WebSocket path.' }
    $key = $null
    while ($line = $headers.ReadLine()) {
        if ($line.StartsWith('Sec-WebSocket-Key:', [StringComparison]::OrdinalIgnoreCase)) { $key = $line.Split(':', 2)[1].Trim() }
    }
    $headers.Dispose()
    if (-not $key) { throw 'Missing WebSocket key.' }
    $accept = [Convert]::ToBase64String([Security.Cryptography.SHA1]::HashData([Text.Encoding]::ASCII.GetBytes($key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11')))
    $response = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 101 Switching Protocols`r`nUpgrade: websocket`r`nConnection: Upgrade`r`nSec-WebSocket-Accept: $accept`r`n`r`n")
    $stream.Write($response)
    $options = [Net.WebSockets.WebSocketCreationOptions]::new()
    $options.IsServer = $true
    $options.KeepAliveInterval = [TimeSpan]::FromSeconds(30)
    $socket = [Net.WebSockets.WebSocket]::CreateFromStream($stream, $options)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes('技能')
        $null = $socket.SendAsync([ArraySegment[byte]]::new($bytes, 0, 1), [Net.WebSockets.WebSocketMessageType]::Text,
            $false, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $null = $socket.SendAsync([ArraySegment[byte]]::new($bytes, 1, $bytes.Length - 1), [Net.WebSockets.WebSocketMessageType]::Text,
            $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $buffer = [byte[]]::new(1024)
        $received = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        if ($received.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text -or -not $received.EndOfMessage -or
            [Text.Encoding]::UTF8.GetString($buffer, 0, $received.Count) -ne '{"kind":"test"}') { throw 'Unexpected client message.' }
        [Console]::WriteLine('RECEIVED')
        try { $null = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).GetAwaiter().GetResult() }
        catch [Net.WebSockets.WebSocketException] { }
    }
    finally { $socket.Dispose(); $connection.Dispose() }
}
finally { $listener.Stop() }
