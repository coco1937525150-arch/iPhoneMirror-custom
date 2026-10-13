[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$File = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_rtp_mirror.c'
if (-not (Test-Path -LiteralPath $File -PathType Leaf)) {
    throw "AirPlay mirror source not found: $File"
}

$Utf8 = [Text.UTF8Encoding]::new($false)
$Lf = [string][char]10
$Text = ([IO.File]::ReadAllText($File, $Utf8)) -replace '\r\n', $Lf
$Marker = 'IPHONE_MIRROR_TEARDOWN_C_GRACEFUL_CLOSE'

function Replace-ExactlyOnce([string]$Needle, [string]$Replacement, [string]$Description) {
    $Matches = [regex]::Matches($script:Text, [regex]::Escape($Needle)).Count
    if ($Matches -ne 1) {
        throw "${Description}: expected one match, found $Matches."
    }
    $script:Text = $script:Text.Replace($Needle, $Replacement)
}

if (-not $Text.Contains($Marker)) {
    # Important: the iPhone already sent RTSP TEARDOWN in the B2 capture
    # before the Windows mirror TCP RST. This code does not attempt to keep
    # an idle iOS mirroring session alive; it only isolates the teardown path.
    $Needle = '#define RAOP_PACKET_LEN 32768'
    $Helper = @'
/* IPHONE_MIRROR_TEARDOWN_C_GRACEFUL_CLOSE
 * Diagnostic variant C: after RTSP/local stop, send our TCP FIN and
 * briefly drain any queued peer data/FIN before closesocket(). Closing a
 * socket while unread inbound bytes remain can create a TCP RST.
 * Bound all work to 24 select iterations, each at most 20 milliseconds.
 */
static void
iphone_mirror_c_close_after_local_stop(raop_rtp_mirror_t *mirror, int fd)
{
    int shutdown_result;
    int peer_fin_seen = 0;
    int drained_bytes = 0;
    int receive_error = 0;
    int iteration;
    unsigned char scratch[4096];

#ifdef WIN32
    shutdown_result = shutdown((SOCKET)fd, SD_SEND);
#else
    shutdown_result = shutdown(fd, SHUT_WR);
#endif
    if (shutdown_result == 0) {
        for (iteration = 0; iteration < 24; ++iteration) {
            fd_set rfds;
            struct timeval timeout;
            int ready;
            int received;

            FD_ZERO(&rfds);
            FD_SET(fd, &rfds);
            timeout.tv_sec = 0;
            timeout.tv_usec = 20000;
            ready = select(fd + 1, &rfds, NULL, NULL, &timeout);
            if (ready == 0) {
                continue;
            }
            if (ready < 0) {
#ifdef WIN32
                receive_error = WSAGetLastError();
#else
                receive_error = errno;
#endif
                break;
            }
            received = recv(fd, (char *)scratch, sizeof(scratch), 0);
            if (received == 0) {
                peer_fin_seen = 1;
                break;
            }
            if (received < 0) {
#ifdef WIN32
                receive_error = WSAGetLastError();
#else
                receive_error = errno;
#endif
                break;
            }
            drained_bytes += received;
        }
    }
#ifdef WIN32
    else {
        receive_error = WSAGetLastError();
    }
#else
    else {
        receive_error = errno;
    }
#endif
    logger_log(mirror->logger, LOGGER_INFO,
        "IPHONE_MIRROR_TEARDOWN_C cause=local_stop shutdown_result=%d peer_fin=%d drained_bytes=%d error=%d",
        shutdown_result, peer_fin_seen, drained_bytes, receive_error);
    closesocket(fd);
}

'@ -replace '\r?\n', $Lf
    Replace-ExactlyOnce $Needle ($Helper + $Needle) 'C teardown helper insertion'

    $Needle = @'
    /* Close the stream file descriptor */
    if (stream_fd != -1) {
        closesocket(stream_fd);
    }
    if (exceptionExit) {
'@ -replace '\r?\n', $Lf

    $Replacement = @'
    /* Variant C: distinguish normal RTSP-initiated local stopping from
     * an actual mirror worker failure. Retain the existing remote-EOF path. */
    int iphone_mirror_c_local_stop;
    MUTEX_LOCK(raop_rtp_mirror->run_mutex);
    iphone_mirror_c_local_stop = !raop_rtp_mirror->running;
    MUTEX_UNLOCK(raop_rtp_mirror->run_mutex);

    if (stream_fd != -1) {
        if (iphone_mirror_c_local_stop) {
            iphone_mirror_c_close_after_local_stop(raop_rtp_mirror, stream_fd);
        } else {
            closesocket(stream_fd);
        }
    }
    if (exceptionExit && iphone_mirror_c_local_stop) {
        logger_log(raop_rtp_mirror->logger, LOGGER_INFO,
            "IPHONE_MIRROR_TEARDOWN_C cause=local_stop_skip_worker_exception");
    }
    if (exceptionExit && !iphone_mirror_c_local_stop) {
'@ -replace '\r?\n', $Lf
    Replace-ExactlyOnce $Needle $Replacement 'C mirror worker close path'

    [IO.File]::WriteAllText($File, $Text, $Utf8)
    Write-Host 'Applied experimental C graceful mirror teardown and stop-race classification.'
} else {
    Write-Host 'Experimental C teardown patch already applied.'
}
