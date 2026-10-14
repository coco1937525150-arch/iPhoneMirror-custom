[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$Utf8 = [Text.UTF8Encoding]::new($false)
$Lf = [string][char]10

function Replace-Once([string]$Text, [string]$Needle,
                      [string]$Replacement, [string]$Description) {
    $count = [regex]::Matches($Text, [regex]::Escape($Needle)).Count
    if ($count -ne 1) {
        throw "$Description changed: expected one match, found $count"
    }
    return $Text.Replace($Needle, $Replacement)
}
function Read-Source([string]$Path) {
    return ([IO.File]::ReadAllText($Path, $Utf8) -replace '\r\n', $Lf)
}
function Write-Source([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, $Utf8)
}

# The October 13 trace ended inside the initial 4-byte mirror header recv(),
# rather than the already-patched partial-payload reader. This patch only
# adds phase/cause telemetry; it does not alter TCP or AirPlay session state.
$mirrorPath = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_rtp_mirror.c'
$mirror = Read-Source $mirrorPath
if (-not $mirror.Contains('IPHONE_MIRROR_MIRROR_DISCONNECT_TRACE')) {
    $mirror = Replace-Once $mirror '    int exceptionExit = 0;' (
        '    /* IPHONE_MIRROR_MIRROR_DISCONNECT_TRACE */' + $Lf +
        '    time_t last_mirror_header_at = time(NULL);' + $Lf +
        '    int exceptionExit = 0;'
    ) 'mirror last-header clock'

    $needle = '            if ((packet[0] == 80 && packet[1] == 79 && packet[2] == 83 && packet[3] == 84)'
    $mirror = Replace-Once $mirror $needle (
        '            last_mirror_header_at = time(NULL);' + $Lf + $needle
    ) 'mirror header activity'

    $needle = @'
            if (ret == 0) {
                /* TCP socket closed */
                logger_log(raop_rtp_mirror->logger, LOGGER_INFO, "TCP socket closed");
                exceptionExit = 1;
                break;
            } else if (ret == -1) {
                /* FIXME: Error happened */
                logger_log(raop_rtp_mirror->logger, LOGGER_INFO, "Error in recv");
                exceptionExit = 1;
                break;
            }
'@ -replace '\r?\n', $Lf
    $replacement = @'
            if (ret == 0) {
                int active;
                MUTEX_LOCK(raop_rtp_mirror->run_mutex);
                active = raop_rtp_mirror->running;
                MUTEX_UNLOCK(raop_rtp_mirror->run_mutex);
                /* recv()==0 is an orderly receive EOF; only a packet capture
                 * proves which endpoint actually originated the TCP FIN. */
                logger_log(raop_rtp_mirror->logger, LOGGER_INFO,
                    "IPHONE_MIRROR_MIRROR_TRACE cause=%s phase=header idle_seconds=%.0f running=%d",
                    active ? "peer_eof_observed" : "eof_during_local_stop",
                    difftime(time(NULL), last_mirror_header_at), active);
                exceptionExit = 1;
                break;
            } else if (ret == -1) {
#ifdef WIN32
                int socket_error = WSAGetLastError();
#else
                int socket_error = errno;
#endif
                logger_log(raop_rtp_mirror->logger, LOGGER_INFO,
                    "IPHONE_MIRROR_MIRROR_TRACE cause=recv_error phase=header idle_seconds=%.0f error=%d",
                    difftime(time(NULL), last_mirror_header_at), socket_error);
                exceptionExit = 1;
                break;
            }
'@ -replace '\r?\n', $Lf
    $mirror = Replace-Once $mirror $needle $replacement 'mirror header socket close'
    $needle = '    raop_rtp_mirror_t* raop_rtp_mirror = arg;' + $Lf +
        '    raop_rtp_mirror_stop(raop_rtp_mirror);'
    $replacement = '    raop_rtp_mirror_t* raop_rtp_mirror = arg;' + $Lf +
        '    logger_log(raop_rtp_mirror->logger, LOGGER_INFO,' + $Lf +
        '        "IPHONE_MIRROR_MIRROR_TRACE cause=worker_exception_stop");' + $Lf +
        '    raop_rtp_mirror_stop(raop_rtp_mirror);'
    $mirror = Replace-Once $mirror $needle $replacement 'mirror exception-thread exit'
    $needle = '    logger_log(raop_rtp_mirror->logger, LOGGER_INFO, "Stopping raop rtp mirror");'
    $mirror = Replace-Once $mirror $needle (
        '    logger_log(raop_rtp_mirror->logger, LOGGER_INFO,' + $Lf +
        '        "IPHONE_MIRROR_MIRROR_TRACE cause=stop_requested");' + $Lf +
        $needle) 'mirror stop entry'
    Write-Source $mirrorPath $mirror
}

$httpdPath = Join-Path $SourceRoot 'AirPlayServerLib\lib\httpd.c'
$httpd = Read-Source $httpdPath
if (-not $httpd.Contains('IPHONE_MIRROR_CONTROL_DISCONNECT_TRACE')) {
    $needle = 'logger_log(httpd->logger, LOGGER_INFO, "Connection closed for socket %d", connection->socket_fd);'
    $replacement = @'
/* IPHONE_MIRROR_CONTROL_DISCONNECT_TRACE */
                logger_log(httpd->logger, LOGGER_INFO,
                    "IPHONE_MIRROR_CONTROL_TRACE cause=peer_eof_observed");
'@ -replace '\r?\n', $Lf
    $httpd = Replace-Once $httpd $needle ($replacement + $Lf + $needle) 'control socket EOF'
    $needle = 'logger_log(httpd->logger, LOGGER_INFO, "Error receiving data on socket %d", connection->socket_fd);'
    $replacement = @'
#ifdef WIN32
                logger_log(httpd->logger, LOGGER_INFO,
                    "IPHONE_MIRROR_CONTROL_TRACE cause=recv_error error=%d", WSAGetLastError());
#else
                logger_log(httpd->logger, LOGGER_INFO,
                    "IPHONE_MIRROR_CONTROL_TRACE cause=recv_error error=%d", errno);
#endif
'@ -replace '\r?\n', $Lf
    $httpd = Replace-Once $httpd $needle ($replacement + $Lf + $needle) 'control socket error'
    Write-Source $httpdPath $httpd
}

$raopPath = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop.c'
$raop = Read-Source $raopPath
if (-not $raop.Contains('IPHONE_MIRROR_RAOP_TEARDOWN_TRACE')) {
    $needle = 'handler = &raop_handler_teardown;'
    $replacement = @'
/* IPHONE_MIRROR_RAOP_TEARDOWN_TRACE */
        logger_log(conn->raop->logger, LOGGER_INFO,
            "IPHONE_MIRROR_CONTROL_TRACE cause=rtsp_teardown_received");
        handler = &raop_handler_teardown;
'@ -replace '\r?\n', $Lf
    $raop = Replace-Once $raop $needle $replacement 'RTSP teardown detection'
    Write-Source $raopPath $raop
}

# Experiment C is a separate baseline-derived diagnostic variant. No B keepAlive change.
# Collect only socket counts/state, never addresses, keys, or payload bytes.
$mirror = Read-Source $mirrorPath
if (-not $mirror.Contains('IPHONE_MIRROR_C_SOCKET_CLOSE_TRACE')) {
    $needle = '    /* Close the stream file descriptor */'
    $replacement = @'
    /* IPHONE_MIRROR_C_SOCKET_CLOSE_TRACE: instrument shutdown without
     * changing ownership, socket options, or close timing. */
#ifdef WIN32
    if (stream_fd != -1) {
        u_long unread = 0;
        int queued = ioctlsocket((SOCKET)stream_fd, FIONREAD, &unread);
        int status_error = 0;
        int status_size = (int)sizeof(status_error);
        int status_result = getsockopt((SOCKET)stream_fd, SOL_SOCKET,
            SO_ERROR, (char *)&status_error, &status_size);
        int already_stopping;
        MUTEX_LOCK(raop_rtp_mirror->run_mutex);
        already_stopping = raop_rtp_mirror->stop_in_progress;
        MUTEX_UNLOCK(raop_rtp_mirror->run_mutex);
        logger_log(raop_rtp_mirror->logger, LOGGER_INFO,
            "IPHONE_MIRROR_C_SOCKET_CLOSE_TRACE phase=worker_close exception=%d stopping=%d unread_ok=%d unread_bytes=%lu so_error_ok=%d so_error=%d",
            exceptionExit, already_stopping, queued == 0,
            queued == 0 ? unread : 0UL, status_result == 0,
            status_result == 0 ? status_error : -1);
    }
#endif
'@ -replace '\r?\n', $Lf
    $mirror = Replace-Once $mirror $needle ($replacement + $Lf + $needle) 'C mirror worker socket-close diagnostics'
    Write-Source $mirrorPath $mirror
}

Write-Host 'Applied diagnostic-only AirPlay lock-screen trace patch.' -ForegroundColor Green
