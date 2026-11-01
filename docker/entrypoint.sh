#!/bin/sh
# Starts a virtual X server and PostSharp in headless mode inside the container.
set -eu

display="${POSTSHARP_DISPLAY:-:99}"
export DISPLAY="$display"

Xvfb "$display" -screen 0 1280x1024x24 -nolisten tcp &
xvfb_pid=$!

stop_xvfb() {
    kill "$xvfb_pid" 2>/dev/null || true
}

attempts=0
while [ ! -S "/tmp/.X11-unix/X${display#:}" ]; do
    attempts=$((attempts + 1))
    if [ "$attempts" -ge 100 ]; then
        echo "Xvfb did not start on $display" >&2
        stop_xvfb
        exit 1
    fi
    sleep 0.1
done

./PostSharp --headless &
app_pid=$!

forward_term() {
    kill -TERM "$app_pid" 2>/dev/null || true
}

trap 'forward_term; stop_xvfb; exit 0' INT TERM

status=0
wait "$app_pid" || status=$?
stop_xvfb
exit "$status"
