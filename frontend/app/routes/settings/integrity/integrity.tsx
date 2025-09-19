import { Alert, Form } from "react-bootstrap";
import styles from "./integrity.module.css"
import { type Dispatch, type SetStateAction } from "react";

type IntegritySettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function IntegritySettings({ config, setNewConfig }: IntegritySettingsProps) {
    // derived variables
    const isEnabled = config["integrity.enabled"] === "true";
    
    // Check if any arr instances are configured
    const hasArrInstances = () => {
        for (let i = 0; i < 10; i++) {
            const radarrUrl = config[`radarr.${i}.url`];
            const radarrApiKey = config[`radarr.${i}.api_key`];
            const sonarrUrl = config[`sonarr.${i}.url`];
            const sonarrApiKey = config[`sonarr.${i}.api_key`];
            
            if ((radarrUrl && radarrApiKey) || (sonarrUrl && sonarrApiKey)) {
                return true;
            }
        }
        return false;
    };

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
                            <option value="delete_via_arr">Delete via Radarr/Sonarr</option>
                        </Form.Select>
                        <Form.Text id="integrity-action-help" muted>
                            What to do when corrupt files are detected. "Log Only" is safest, "Quarantine" moves files to a quarantine folder, "Delete" permanently removes corrupt files, "Delete via Radarr/Sonarr" removes files through configured Radarr/Sonarr instances (configure in Radarr/Sonarr tab).
                        </Form.Text>
                    </Form.Group>
                </>
            )}

            {config["integrity.corrupt_file_action"] === "delete_via_arr" && (
                <>
                    <hr />
                    {!hasArrInstances() ? (
                        <Alert variant="warning">
                            <strong>Radarr/Sonarr Integration Required:</strong> You have selected "Delete via Radarr/Sonarr" but need to configure your instances first. 
                            Please go to the <strong>Radarr/Sonarr</strong> tab in Advanced Settings to set up your instances.
                        </Alert>
                    ) : (
                        <Alert variant="success">
                            <strong>Radarr/Sonarr Integration Ready:</strong> Your configured instances will be used to delete corrupt files. 
                            You can manage your instances in the <strong>Radarr/Sonarr</strong> tab in Advanced Settings.
                        </Alert>
                    )}
                    <hr />
                    <Form.Group>
                        <Form.Check
                            type="checkbox"
                            id="direct-deletion-fallback-checkbox"
                            label="Enable direct deletion fallback"
                            checked={config["integrity.direct_deletion_fallback"] === "true"}
                            onChange={e => setNewConfig({ ...config, "integrity.direct_deletion_fallback": e.target.checked ? "true" : "false" })}
                        />
                        <Form.Text muted>
                            When enabled, if Radarr/Sonarr cannot delete a corrupt file (e.g., file not found in their library), 
                            the system will delete the file directly from the filesystem. Leave disabled for safety.
                        </Form.Text>
                    </Form.Group>
                    
                    <hr />
                    
                    <Form.Group>
                        <Form.Check
                            type="checkbox"
                            id="auto-unmonitor-checkbox"
                            label="Auto-unmonitor after successful integrity checks"
                            checked={config["integrity.auto_unmonitor"] === "true"}
                            onChange={e => setNewConfig({ ...config, "integrity.auto_unmonitor": e.target.checked ? "true" : "false" })}
                        />
                        <Form.Text muted>
                            When enabled, movies and TV series will be automatically unmonitored in Radarr/Sonarr after passing integrity checks. 
                            This prevents re-downloading of verified files. Only applies when using Radarr/Sonarr integration.
                        </Form.Text>
                    </Form.Group>
                </>
            )}

            <hr />

            <Form.Group>
                <Form.Check
                    type="checkbox"
                    id="mp4-deep-scan-checkbox"
                    label="Enable MP4 deep scan"
                    checked={config["integrity.mp4_deep_scan"] === "true"}
                    onChange={e => setNewConfig({ ...config, "integrity.mp4_deep_scan": e.target.checked ? "true" : "false" })}
                />
                <Form.Text muted>
                    When enabled, MP4 files use a slower but more thorough validation method that can detect moov atom issues. 
                    Disable for faster validation if you don't have MP4 corruption issues.
                </Form.Text>
            </Form.Group>

        </div>
    );
}

export function isIntegritySettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["integrity.enabled"] !== newConfig["integrity.enabled"]
        || config["integrity.interval_hours"] !== newConfig["integrity.interval_hours"]
        || config["integrity.interval_days"] !== newConfig["integrity.interval_days"]
        || config["integrity.max_files_per_run"] !== newConfig["integrity.max_files_per_run"]
        || config["integrity.corrupt_file_action"] !== newConfig["integrity.corrupt_file_action"]
        || config["integrity.direct_deletion_fallback"] !== newConfig["integrity.direct_deletion_fallback"]
        || config["integrity.auto_unmonitor"] !== newConfig["integrity.auto_unmonitor"]
        || config["integrity.mp4_deep_scan"] !== newConfig["integrity.mp4_deep_scan"];
}
