import { Alert, Button, Form } from "react-bootstrap";
import styles from "./integrity.module.css"
import { useCallback, useEffect, useState, type Dispatch, type SetStateAction } from "react";
import { receiveMessage } from "~/utils/websocket-util";

const integrityProgressTopic = { 'icp': 'state' };

type IntegritySettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function IntegritySettings({ config, setNewConfig }: IntegritySettingsProps) {
    // stateful variables
    const [connected, setConnected] = useState<boolean>(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [isFetching, setIsFetching] = useState<boolean>(false);

    // derived variables
    const isEnabled = config["integrity.enabled"] === "true";
    const isFinished = progress?.startsWith("complete") || progress?.startsWith("failed") || progress?.startsWith("cancelled");
    const isRunning = !isFinished && (isFetching || progress !== null);
    const isRunButtonEnabled = connected && !isRunning;
    const runButtonVariant = isRunButtonEnabled ? 'primary' : 'secondary';
    const runButtonLabel = isRunning ? "⌛ Checking..." : '🔍 Check Media Integrity Now';

    // Parse progress for display
    let progressDetails = null;
    if (isRunning && progress && progress.includes('/')) {
        const parts = progress.split(' ');
        const fraction = parts[0];
        const corruptInfo = parts.length > 1 ? parts.slice(1).join(' ') : '';
        progressDetails = { fraction, corruptInfo };
    }

    // effects
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            const wsUrl = `${protocol}//${window.location.host}/ws`;
            ws = new WebSocket(wsUrl);
            ws.onmessage = receiveMessage((_, message) => setProgress(message));
            ws.onopen = () => { 
                setConnected(true); 
                ws.send(JSON.stringify(integrityProgressTopic)); 
            }
            ws.onclose = () => { 
                setConnected(false);
                if (!disposed) setTimeout(() => connect(), 1000); 
                setProgress(null) 
            };
            ws.onerror = () => { ws.close() };
        }
        connect();
        return () => { disposed = true; ws.close(); }
    }, [setProgress, setConnected]);

    // events
    const onRunCheck = useCallback(async () => {
        setIsFetching(true);
        try {
            const response = await fetch("/api/media-integrity", {
                method: "POST",
                headers: {
                    'Content-Type': 'application/json'
                }
            });
            if (!response.ok) {
                console.error('Failed to trigger integrity check:', response.statusText);
            }
        } catch (error) {
            console.error('Error triggering integrity check:', error);
        }
        setIsFetching(false);
    }, [setIsFetching]);

    // view
    return (
        <div className={styles.container}>
            {!isEnabled && (
                <Alert variant="info">
                    <strong>Media Integrity Verification is disabled.</strong> Enable it below to automatically check your media files for corruption.
                </Alert>
            )}

            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="integrity-enabled-checkbox"
                    aria-describedby="integrity-enabled-help"
                    label="Enable Media Integrity Verification"
                    checked={isEnabled}
                    onChange={e => setNewConfig({ ...config, "integrity.enabled": "" + e.target.checked })} />
                <Form.Text id="integrity-enabled-help" muted>
                    Automatically check media files for corruption using ffprobe. Files are checked in the background at configurable intervals.
                </Form.Text>
            </Form.Group>

            {isEnabled && (
                <>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="integrity-interval-input">Check Interval (hours)</Form.Label>
                        <Form.Control
                            className={styles.input}
                            type="number"
                            id="integrity-interval-input"
                            aria-describedby="integrity-interval-help"
                            min="1"
                            max="168"
                            placeholder="24"
                            value={config["integrity.interval_hours"]}
                            onChange={e => setNewConfig({ ...config, "integrity.interval_hours": e.target.value })} />
                        <Form.Text id="integrity-interval-help" muted>
                            How often the background integrity check runs (1-168 hours).
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="integrity-recheck-input">Re-check Files After (days)</Form.Label>
                        <Form.Control
                            className={styles.input}
                            type="number"
                            id="integrity-recheck-input"
                            aria-describedby="integrity-recheck-help"
                            min="1"
                            max="365"
                            placeholder="7"
                            value={config["integrity.interval_days"]}
                            onChange={e => setNewConfig({ ...config, "integrity.interval_days": e.target.value })} />
                        <Form.Text id="integrity-recheck-help" muted>
                            How many days to wait before re-checking files that have already been verified (1-365 days).
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="integrity-max-files-input">Max Files Per Run</Form.Label>
                        <Form.Control
                            className={styles.input}
                            type="number"
                            id="integrity-max-files-input"
                            aria-describedby="integrity-max-files-help"
                            min="1"
                            max="1000"
                            placeholder="100"
                            value={config["integrity.max_files_per_run"]}
                            onChange={e => setNewConfig({ ...config, "integrity.max_files_per_run": e.target.value })} />
                        <Form.Text id="integrity-max-files-help" muted>
                            Maximum number of files to check in a single run to avoid overwhelming the system (1-1000).
                        </Form.Text>
                    </Form.Group>
                    <hr />
                    <Form.Group>
                        <Form.Label htmlFor="integrity-action-select">Action for Corrupt Files</Form.Label>
                        <Form.Select
                            className={styles.input}
                            id="integrity-action-select"
                            aria-describedby="integrity-action-help"
                            value={config["integrity.corrupt_file_action"]}
                            onChange={e => setNewConfig({ ...config, "integrity.corrupt_file_action": e.target.value })}>
                            <option value="log">Log Only</option>
                            <option value="quarantine">Move to Quarantine</option>
                            <option value="delete">Delete Files</option>
                        </Form.Select>
                        <Form.Text id="integrity-action-help" muted>
                            What to do when corrupt files are detected. "Log Only" is safest, "Quarantine" moves files to a quarantine folder, "Delete" permanently removes corrupt files.
                        </Form.Text>
                    </Form.Group>
                </>
            )}

            <hr />

            <div className={styles.task}>
                <Form.Group>
                    <Form.Label className={styles.title}>Manual Integrity Check</Form.Label>
                    <div className={styles.run}>
                        <Button variant={runButtonVariant} onClick={onRunCheck} disabled={!isRunButtonEnabled}>
                            {runButtonLabel}
                        </Button>
                        {isRunning && progressDetails && (
                            <div className={styles["task-progress"]}>
                                Progress: {progressDetails.fraction} <br />
                                {progressDetails.corruptInfo && <span>Status: {progressDetails.corruptInfo}</span>}
                            </div>
                        )}
                        {isRunning && progress && !progressDetails && (
                            <div className={styles["task-progress"]}>
                                Status: {progress}
                            </div>
                        )}
                        {isFinished && (
                            <div className={styles["task-progress"]}>
                                {progress}
                            </div>
                        )}
                        {!connected && (
                            <div className={styles["task-progress"]}>
                                <em>Connecting to status updates...</em>
                            </div>
                        )}
                    </div>
                    <Form.Text id="manual-check-help" muted>
                        <br />
                        Manually trigger an integrity check to verify media files immediately. 
                        This will check files that haven't been verified recently according to your settings.
                        <br /><br />
                        The check uses ffprobe to analyze video and audio files for corruption. 
                        Supported formats include MP4, MKV, AVI, MP3, FLAC, and many others.
                    </Form.Text>
                </Form.Group>
            </div>
        </div>
    );
}

export function isIntegritySettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["integrity.enabled"] !== newConfig["integrity.enabled"]
        || config["integrity.interval_hours"] !== newConfig["integrity.interval_hours"]
        || config["integrity.interval_days"] !== newConfig["integrity.interval_days"]
        || config["integrity.max_files_per_run"] !== newConfig["integrity.max_files_per_run"]
        || config["integrity.corrupt_file_action"] !== newConfig["integrity.corrupt_file_action"];
}
