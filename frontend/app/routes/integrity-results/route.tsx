import type { Route } from "./+types/route";
import styles from "./route.module.css";
import { useState, useEffect, useCallback } from "react";
import { Card, Table, Badge, Alert, Button, Collapse } from "react-bootstrap";
import { receiveMessage } from "../../utils/websocket-util";

const integrityProgressTopic = { 'icp': 'state' };

// Integrity Check Button Component
function IntegrityCheckButton() {
    const [isFetching, setIsFetching] = useState(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [connected, setConnected] = useState(false);

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

    // WebSocket connection effect
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
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

    // Trigger integrity check
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

    return (
        <div className="d-flex align-items-center gap-3">
            <Button variant={runButtonVariant} onClick={onRunCheck} disabled={!isRunButtonEnabled}>
                {runButtonLabel}
            </Button>
            {isRunning && progressDetails && (
                <div className="text-muted small">
                    Progress: {progressDetails.fraction} 
                    {progressDetails.corruptInfo && <span> - {progressDetails.corruptInfo}</span>}
                </div>
            )}
            {isRunning && progress && !progressDetails && (
                <div className="text-muted small">
                    Status: {progress}
                </div>
            )}
            {isFinished && (
                <div className="text-muted small">
                    {progress}
                </div>
            )}
            {!connected && (
                <div className="text-muted small">
                    <em>Connecting to status updates...</em>
                </div>
            )}
        </div>
    );
}

// Helper function to format UTC dates for local display
function formatDateForDisplay(dateString: string): string {
    // Backend sends UTC dates, convert to local time for display
    const date = new Date(dateString);
    return date.toLocaleDateString();
}

// Helper function to format UTC timestamps for local display  
function formatTimestampForDisplay(dateString: string): string {
    // Backend sends UTC timestamps, convert to local time for display
    const date = new Date(dateString);
    return date.toLocaleString();
}

// Helper function to calculate and format duration
function formatDuration(startTime?: string, endTime?: string): string {
    if (!startTime || !endTime) {
        return "Unknown";
    }
    
    const start = new Date(startTime);
    const end = new Date(endTime);
    const durationMs = end.getTime() - start.getTime();
    
    if (durationMs < 0) {
        return "Invalid";
    }
    
    const seconds = Math.floor(durationMs / 1000);
    const minutes = Math.floor(seconds / 60);
    const hours = Math.floor(minutes / 60);
    
    if (hours > 0) {
        return `${hours}h ${minutes % 60}m ${seconds % 60}s`;
    } else if (minutes > 0) {
        return `${minutes}m ${seconds % 60}s`;
    } else {
        return `${seconds}s`;
    }
}

type IntegrityFileResult = {
    fileId: string;
    filePath: string;
    fileName: string;
    isLibraryFile: boolean;
    lastChecked: string;
    status: string;
    errorMessage?: string;
    actionTaken?: string;
    runId?: string;
};

type IntegrityJobRun = {
    date: string;
    runId?: string;
    startTime?: string;
    endTime?: string;
    totalFiles: number;
    corruptFiles: number;
    validFiles: number;
    files: IntegrityFileResult[];
};

type IntegrityResultsData = {
    jobRuns: IntegrityJobRun[];
    allFiles: IntegrityFileResult[];
};

export async function loader({ request }: Route.LoaderArgs) {
    try {
        const url = new URL(request.url);
        const backendUrl = process.env.BACKEND_URL || `${url.protocol}//${url.host}`;
        const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
        
        const response = await fetch(`${backendUrl}/api/integrity-results`, {
            method: "GET",
            headers: {
                "Content-Type": "application/json",
                "x-api-key": apiKey
            }
        });

        if (!response.ok) {
            const errorText = await response.text();
            console.error("API Error:", response.status, errorText);
            return { data: null, error: `Failed to load integrity results: ${response.status} ${response.statusText}` };
        }

        const data: IntegrityResultsData = await response.json();
        return { data, error: null };
    } catch (error) {
        console.error("Failed to load integrity results:", error);
        return { data: null, error: "Failed to load integrity results" };
    }
}

export default function IntegrityResults(props: Route.ComponentProps) {
    const { data, error } = props.loaderData;
    const [liveData, setLiveData] = useState(data);
    const [isCheckRunning, setIsCheckRunning] = useState(false);
    const [lastProgressUpdate, setLastProgressUpdate] = useState<string | null>(null);
    const [isCancelling, setIsCancelling] = useState(false);

    // Function to refresh integrity data
    const refreshIntegrityData = useCallback(async () => {
        try {
            const url = new URL(window.location.href);
            const backendUrl = process.env.BACKEND_URL || `${url.protocol}//${url.host}`;
            const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
            
            const response = await fetch(`${backendUrl}/api/integrity-results`, {
                method: "GET",
                headers: {
                    "Content-Type": "application/json",
                    "x-api-key": apiKey
                }
            });
            
            if (response.ok) {
                const freshData = await response.json();
                setLiveData(freshData);
            }
        } catch (error) {
            console.error("Failed to refresh integrity data:", error);
        }
    }, []);

    // Function to cancel integrity check
    const cancelIntegrityCheck = useCallback(async () => {
        setIsCancelling(true);
        try {
            const url = new URL(window.location.href);
            const backendUrl = process.env.BACKEND_URL || `${url.protocol}//${url.host}`;
            const apiKey = process.env.FRONTEND_BACKEND_API_KEY || "";
            
            const response = await fetch(`${backendUrl}/api/media-integrity/cancel`, {
                method: "POST",
                headers: {
                    "Content-Type": "application/json",
                    "x-api-key": apiKey
                }
            });
            
            if (response.ok) {
                const result = await response.json();
                console.log("Cancel result:", result);
            } else {
                console.error("Failed to cancel integrity check:", response.statusText);
            }
        } catch (error) {
            console.error("Error cancelling integrity check:", error);
        } finally {
            setIsCancelling(false);
        }
    }, []);

    // WebSocket for real-time updates
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        let refreshInterval: NodeJS.Timeout | null = null;
        
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            
            ws.onmessage = receiveMessage((_, message) => {
                setLastProgressUpdate(message);
                
                // Determine if check is running based on progress message
                const isRunning = message && 
                    !message.startsWith("complete") && 
                    !message.startsWith("failed") && 
                    !message.startsWith("cancelled");
                    
                setIsCheckRunning(isRunning);
                
                // Reset cancelling state when check completes
                if (!isRunning) {
                    setIsCancelling(false);
                }
                
                // When check starts, refresh data to get the new run
                if (message === "starting") {
                    // Immediate refresh plus delayed refresh to catch the new run
                    refreshIntegrityData();
                    setTimeout(() => refreshIntegrityData(), 2000); // Give backend time to create the run
                    
                    // Start periodic refresh during check to show new files
                    refreshInterval = setInterval(() => {
                        refreshIntegrityData();
                    }, 3000); // Refresh every 3 seconds during check for more responsive updates
                }
                
                // When check completes, stop periodic refresh and do final refresh
                if (message && (message.startsWith("complete") || message.startsWith("failed") || message.startsWith("cancelled"))) {
                    if (refreshInterval) {
                        clearInterval(refreshInterval);
                        refreshInterval = null;
                    }
                    
                    setTimeout(() => {
                        refreshIntegrityData();
                    }, 2000);
                }
            });
            
            ws.onopen = () => { 
                ws.send(JSON.stringify(integrityProgressTopic)); 
            };
            
            ws.onclose = () => { 
                if (!disposed) setTimeout(() => connect(), 1000); 
            };
            
            ws.onerror = () => { ws.close() };
        }
        
        connect();
        return () => { 
            disposed = true; 
            ws.close(); 
            if (refreshInterval) {
                clearInterval(refreshInterval);
            }
        };
    }, [refreshIntegrityData]);

    // Update live data when props data changes
    useEffect(() => {
        setLiveData(data);
    }, [data]);

    if (error) {
        return (
            <div className="container mt-4">
                <Alert variant="danger">
                    <Alert.Heading>Error Loading Integrity Results</Alert.Heading>
                    <p>{error}</p>
                </Alert>
            </div>
        );
    }

    if (!liveData) {
        return (
            <div className="container mt-4">
                <Alert variant="info">
                    <Alert.Heading>No Integrity Check Data</Alert.Heading>
                    <p>No integrity checks have been run yet. Enable integrity checking in Settings to start monitoring your media files.</p>
                </Alert>
            </div>
        );
    }

    return (
        <div className="container mt-4">
            <div className="d-flex justify-content-between align-items-center mb-4">
                <h1>Media Integrity Results</h1>
                <IntegrityCheckButton />
            </div>
            
            
            {liveData.jobRuns.length === 0 ? (
                <Alert variant="info">
                    <Alert.Heading>No Integrity Checks Found</Alert.Heading>
                    <p>No integrity checks have been completed yet. Check your settings to enable integrity checking.</p>
                </Alert>
            ) : (
                <div className="mb-4">
                    <h3>Integrity Check Results by Execution</h3>
                    <JobRunsList jobRuns={liveData.jobRuns} isCheckRunning={isCheckRunning} />
                </div>
            )}
        </div>
    );
}

function JobRunsList({ jobRuns, isCheckRunning }: { jobRuns: IntegrityJobRun[]; isCheckRunning?: boolean }) {
    const [expandedRuns, setExpandedRuns] = useState<Set<string>>(new Set());

    const toggleRun = (date: string) => {
        const newExpanded = new Set(expandedRuns);
        if (newExpanded.has(date)) {
            newExpanded.delete(date);
        } else {
            newExpanded.add(date);
        }
        setExpandedRuns(newExpanded);
    };

        // Find the run that's most likely active
        const findActiveRun = () => {
            if (!isCheckRunning || jobRuns.length === 0) {
                console.debug("No active run - isCheckRunning:", isCheckRunning, "jobRuns.length:", jobRuns.length);
                return null;
            }
            
            // First priority: look for a run that has a start time but no end time (actively running)
            for (const run of jobRuns) {
                if (run.startTime && !run.endTime) {
                    console.debug("Found active run (no end time):", run.runId, "started:", run.startTime);
                    return run; // This is definitely an active run
                }
            }
            
            // Second priority: if WebSocket says check is running, find the most recent run
            // This handles cases where the run data hasn't been refreshed yet with end time
            const now = new Date();
            const thirtyMinutesAgo = new Date(now.getTime() - 30 * 60 * 1000); // Extend window for longer checks
            
            for (const run of jobRuns) {
                const runTime = run.startTime ? new Date(run.startTime) : new Date(run.date);
                // If the run started within the last 30 minutes and we have an active check, assume it's active
                if (runTime >= thirtyMinutesAgo) {
                    console.debug("Found recent run as active:", run.runId, "started:", run.startTime || run.date);
                    return run;
                }
            }
            
            console.debug("No active run found despite check running. Runs:", jobRuns.map(r => ({ 
                runId: r.runId, 
                startTime: r.startTime, 
                endTime: r.endTime,
                date: r.date 
            })));
            
            // If check is running but no recent runs found, don't show any as active
            // This prevents showing old runs as active when a new check just started
            return null;
        };

    const activeRun = findActiveRun();

    return (
        <div>
            {jobRuns.map((run, index) => {
                const isActiveRun = isCheckRunning && activeRun && run.runId === activeRun.runId;
                
                return (
                    <Card key={run.date} className={`mb-3 ${isActiveRun ? 'border-primary' : ''}`}>
                        <Card.Header className={isActiveRun ? 'bg-primary bg-opacity-10' : ''}>
                            <div className="d-flex justify-content-between align-items-center">
                                <div>
                                    <div className="d-flex align-items-center mb-1">
                                        {isActiveRun && (
                                            <div className="spinner-border spinner-border-sm me-2" role="status">
                                                <span className="visually-hidden">Loading...</span>
                                            </div>
                                        )}
                                        <strong className="h6 mb-0">Integrity Check Execution</strong>
                                        {isActiveRun && (
                                            <span className="ms-2 badge bg-primary">
                                                🔍 Active
                                            </span>
                                        )}
                                    </div>
                                    
                                    <div className="text-muted small">
                                        {run.startTime ? (
                                            <div>
                                                <strong>Started:</strong> {formatTimestampForDisplay(run.startTime)}
                                                {run.endTime && (
                                                    <>
                                                        {" | "}
                                                        <strong>Completed:</strong> {formatTimestampForDisplay(run.endTime)}
                                                        {" | "}
                                                        <strong>Duration:</strong> {formatDuration(run.startTime, run.endTime)}
                                                    </>
                                                )}
                                                {isActiveRun && !run.endTime && (
                                                    <>
                                                        {" | "}
                                                        <span className="text-primary">
                                                            <strong>Running...</strong> (Duration: {formatDuration(run.startTime, new Date().toISOString())})
                                                        </span>
                                                    </>
                                                )}
                                            </div>
                                        ) : (
                                            <div>
                                                <strong>Date:</strong> {formatTimestampForDisplay(run.date)}
                                            </div>
                                        )}
                                        
                                        {run.runId && (
                                            <div className="mt-1">
                                                <strong>Run ID:</strong> {run.runId.substring(0, 8)}...
                                            </div>
                                        )}
                                    </div>
                                    
                                    <div className="mt-2">
                                        <span className="fw-bold">
                                            {run.totalFiles} files checked
                                            {isActiveRun && <span className="text-muted"> (updating...)</span>}
                                        </span>
                                    </div>
                                </div>
                            <div>
                                <Badge bg="success" className="me-2">
                                    {run.validFiles} valid
                                </Badge>
                                {run.corruptFiles > 0 && (
                                    <Badge bg="danger" className="me-2">
                                        {run.corruptFiles} corrupt
                                    </Badge>
                                )}
                                <div className="d-flex gap-2">
                                    <Button
                                        variant="outline-secondary"
                                        size="sm"
                                        onClick={() => toggleRun(run.date)}
                                    >
                                        {expandedRuns.has(run.date) ? "Hide" : "Show"} Files
                                    </Button>
                                    {isActiveRun && isCheckRunning && (
                                        <Button
                                            variant="outline-danger"
                                            size="sm"
                                            onClick={cancelIntegrityCheck}
                                            disabled={isCancelling}
                                        >
                                            {isCancelling ? (
                                                <>
                                                    <div className="spinner-border spinner-border-sm me-1" role="status">
                                                        <span className="visually-hidden">Cancelling...</span>
                                                    </div>
                                                    Cancelling...
                                                </>
                                            ) : (
                                                <>✕ Cancel</>
                                            )}
                                        </Button>
                                    )}
                                </div>
                            </div>
                        </div>
                    </Card.Header>
                    <Collapse in={expandedRuns.has(run.date)}>
                        <Card.Body>
                            <FilesTable files={run.files} />
                        </Card.Body>
                    </Collapse>
                </Card>
                );
            })}
        </div>
    );
}

function FilesTable({ files }: { files: IntegrityFileResult[] }) {
    if (files.length === 0) {
        return <p>No files found.</p>;
    }

    return (
        <Table striped bordered hover responsive>
            <thead>
                <tr>
                    <th>Status</th>
                    <th>File Name</th>
                    <th>File Path</th>
                    <th>Last Checked</th>
                    <th>Error</th>
                    <th>Action Taken</th>
                </tr>
            </thead>
            <tbody>
                {files.map((file) => (
                    <tr key={file.fileId}>
                        <td>
                            <StatusBadge status={file.status} />
                        </td>
                        <td>{file.fileName}</td>
                        <td>
                            <code className="text-muted">{file.filePath}</code>
                        </td>
                        <td>
                            {formatTimestampForDisplay(file.lastChecked)}
                        </td>
                        <td>
                            {file.errorMessage ? (
                                <span className="text-danger" title={file.errorMessage}>
                                    {file.errorMessage.length > 50 
                                        ? `${file.errorMessage.substring(0, 50)}...` 
                                        : file.errorMessage}
                                </span>
                            ) : (
                                <span className="text-muted">-</span>
                            )}
                        </td>
                        <td>
                            {file.actionTaken ? (
                                <span className="text-info" title={file.actionTaken}>
                                    {file.actionTaken.length > 30 
                                        ? `${file.actionTaken.substring(0, 30)}...` 
                                        : file.actionTaken}
                                </span>
                            ) : (
                                <span className="text-muted">-</span>
                            )}
                        </td>
                    </tr>
                ))}
            </tbody>
        </Table>
    );
}

function StatusBadge({ status }: { status: string }) {
    let variant: string;
    let icon: string;
    
    switch (status.toLowerCase()) {
        case "valid":
            variant = "success";
            icon = "✓";
            break;
        case "corrupt":
            variant = "danger";
            icon = "✗";
            break;
        default:
            variant = "warning";
            icon = "?";
    }

    return (
        <Badge bg={variant}>
            {icon} {status}
        </Badge>
    );
}
