#requires -Version 7.4
# Forward only fixed notices. The fixture owner validates the complete report
# independently before it accepts a timing sample or decides on disk cleanup.
function Read-ProductTimingReport {
    param(
        [Parameter(Mandatory)][IO.StreamReader]$Reader,
        [Parameter(Mandatory)][ValidateSet('FO','FOOF')][string]$Order,
        [Parameter(Mandatory)][Text.StringBuilder]$Report,
        [Parameter(Mandatory)][Threading.CancellationToken]$Cancellation
    )
    $fastStart = '{"revision":"personal-ntfs-product-timing-v1","kind":"stage","mode":"F","stage":"Bridge_LaunchStarting"}'
    $ordinaryStart = '{"revision":"personal-ntfs-product-timing-v1","kind":"stage","mode":"O","stage":"Ordinary_Start"}'
    $limit = 131072 * $Order.Length
    $buffer = [char[]]::new(4096)
    $memory = [Memory[char]]::new($buffer)
    $round = 0
    $lineStart = 0
    while ($true) {
        $Cancellation.ThrowIfCancellationRequested()
        # Process stdout can use a synchronous anonymous pipe on Windows.
        # Bound the wait even when that pipe cannot cancel an in-flight read.
        $read = $Reader.ReadAsync($memory, $Cancellation).AsTask().WaitAsync($Cancellation).GetAwaiter().GetResult()
        if ($read -eq 0) { break }
        $offset = $Report.Length
        if ($read -gt $limit - $offset) { throw 'PRODUCT_TIMING_REPORT_LIMIT' }
        $chunk = [string]::new($buffer, 0, $read)
        [void]$Report.Append($chunk)
        for ($end = $chunk.IndexOf("`n"); $end -ge 0; $end = $chunk.IndexOf("`n", $end + 1)) {
            $length = $offset + $end - $lineStart
            # Avoid parsing or echoing samples. Only match the timing host's
            # two exact, single-line start records, including split writes.
            if ($round -lt $Order.Length -and $length -le $fastStart.Length + 1) {
                $line = $Report.ToString($lineStart, $length).TrimEnd([char]13)
                if ($Order[$round] -ceq 'F' -and $line -ceq $fastStart) {
                    $round++
                    Write-Output "PRODUCT_TIMING: Fast scan $round/$($Order.Length) requesting administrator permission"
                } elseif ($Order[$round] -ceq 'O' -and $line -ceq $ordinaryStart) {
                    $round++
                }
            }
            $lineStart = $offset + $end + 1
        }
    }
    if ([Text.Encoding]::UTF8.GetByteCount($Report.ToString()) -gt $limit) { throw 'PRODUCT_TIMING_REPORT_LIMIT' }
}
