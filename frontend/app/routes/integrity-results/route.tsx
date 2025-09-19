import type { Route } from "./+types/route";
import styles from "./route.module.css";
import { useState, useEffect, useCallback, useMemo } from "react";
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
                    'Content-Type': 'application/json;charset=UTF-8'
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
            <Button variant={runButtonVariant} onClick={onRunCheck} disabled={!isRunButtonEnabled}>
                {runButtonLabel}
            </Button>
        </div>
    );
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
        const cookieHeader = request.headers.get("Cookie");
        
        // Use frontend proxy instead of direct backend call - construct absolute URL for server-side rendering
        const response = await fetch(`${url.protocol}//${url.host}/api/integrity-results`, {
            method: "GET",
            headers: {
                "Content-Type": "application/json;charset=UTF-8",
                "Cookie": cookieHeader || "" // Pass cookie for authentication
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
        return { data: null, error: `Failed to load integrity results: ${error instanceof Error ? error.message : String(error)}` };
    }
}

export default function IntegrityResults(props: Route.ComponentProps) {
    const { data, error } = props.loaderData;
    const [liveData, setLiveData] = useState(data);
    const [isCheckRunning, setIsCheckRunning] = useState(false);
    const [lastProgressUpdate, setLastProgressUpdate] = useState<string | null>(null);
    const [isCancelling, setIsCancelling] = useState(false);
    const [currentRunId, setCurrentRunId] = useState<string | null>(null);
    
    // Search and filter state
    const [searchQuery, setSearchQuery] = useState("");
    const [debouncedSearchQuery, setDebouncedSearchQuery] = useState("");
    const [timeFilter, setTimeFilter] = useState("< 24 hours");

    // Function to refresh integrity data
    const refreshIntegrityData = useCallback(async () => {
        try {
            const response = await fetch("/api/integrity-results", {
                method: "GET",
                headers: {
                    "Content-Type": "application/json;charset=UTF-8"
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
            const response = await fetch("/api/media-integrity/cancel", {
                method: "POST",
                headers: {
                    "Content-Type": "application/json;charset=UTF-8"
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
                // Parse message and run ID (format: "message:runId")
                const parts = message ? message.split(':') : [];
                const messageText = parts[0] || message || '';
                const runId = parts.length > 1 ? parts[parts.length - 1] : null; // Take last part as run ID
                
                setLastProgressUpdate(messageText);
                
                // Determine if check is running based on progress message
                const isRunning = !!messageText && 
                    !messageText.startsWith("complete") && 
                    !messageText.startsWith("failed") && 
                    !messageText.startsWith("cancelled");
                    
                setIsCheckRunning(isRunning);
                
                // Update current run ID when check starts or continues
                if (runId && isRunning) {
                    setCurrentRunId(runId);
                    console.debug("Active run ID set to:", runId);
                }
                
                // Reset states when check completes
                if (!isRunning) {
                    setIsCancelling(false);
                    setCurrentRunId(null);
                    console.debug("Check completed, clearing run ID");
                }
                
                // When check starts, refresh data to get the new run
                if (messageText === "starting") {
                    // Immediate refresh plus delayed refresh to catch the new run
                    refreshIntegrityData();
                    setTimeout(() => refreshIntegrityData(), 2000); // Give backend time to create the run
                    
                    // Start periodic refresh during check to show new files
                    refreshInterval = setInterval(() => {
                        refreshIntegrityData();
                    }, 3000); // Refresh every 3 seconds during check for more responsive updates
                }
                
                // When check completes, stop periodic refresh and do final refresh
                if (messageText && (messageText.startsWith("complete") || messageText.startsWith("failed") || messageText.startsWith("cancelled"))) {
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

    // Debounce search query
    useEffect(() => {
        const timer = setTimeout(() => {
            setDebouncedSearchQuery(searchQuery);
        }, 300); // 300ms delay

        return () => clearTimeout(timer);
    }, [searchQuery]);

    // Helper function to get time filter cutoff
    const getTimeFilterCutoff = useCallback(() => {
        const now = new Date();
        switch (timeFilter) {
            case "< 2 hours":
                return new Date(now.getTime() - 2 * 60 * 60 * 1000);
            case "< 24 hours":
                return new Date(now.getTime() - 24 * 60 * 60 * 1000);
            case "< 3 days":
                return new Date(now.getTime() - 3 * 24 * 60 * 60 * 1000);
            case "< 7 days":
                return new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000);
            default:
                return new Date(now.getTime() - 24 * 60 * 60 * 1000);
        }
    }, [timeFilter]);

    // Filter and search logic
    const filteredData = useMemo(() => {
        if (!liveData || !liveData.jobRuns) return liveData;

        const cutoffTime = getTimeFilterCutoff();
        
        // Filter job runs by time
        const timeFilteredRuns = liveData.jobRuns.filter(run => {
            const runTime = run.startTime ? new Date(run.startTime) : new Date(run.date);
            return runTime >= cutoffTime;
        });

        // If no search query, return time-filtered runs
        if (!debouncedSearchQuery.trim()) {
            return {
                ...liveData,
                jobRuns: timeFilteredRuns
            };
        }

        // Apply search filter to files within each run
        const searchLower = debouncedSearchQuery.toLowerCase();
        const searchFilteredRuns = timeFilteredRuns.map(run => {
            const filteredFiles = run.files.filter(file => 
                file.fileName.toLowerCase().includes(searchLower) ||
                file.filePath.toLowerCase().includes(searchLower)
            );
            
            return {
                ...run,
                files: filteredFiles,
                totalFiles: filteredFiles.length,
                validFiles: filteredFiles.filter(f => f.status === 'valid').length,
                corruptFiles: filteredFiles.filter(f => f.status === 'corrupt').length
            };
        }).filter(run => run.files.length > 0); // Only show runs that have matching files

        return {
            ...liveData,
            jobRuns: searchFilteredRuns
        };
    }, [liveData, debouncedSearchQuery, timeFilter, getTimeFilterCutoff]);

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
            
            {/* Search and Filter Controls */}
            <div className="row mb-4">
                <div className="col-md-8">
                    <div className="input-group">
                        <span className="input-group-text">
                            <svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
                                <path d="M11.742 10.344a6.5 6.5 0 1 0-1.397 1.398h-.001l3.85 3.85a1 1 0 0 0 1.415-1.414l-3.85-3.85zm-5.442 1.5a5.5 5.5 0 1 1 0-11 5.5 5.5 0 0 1 0 11"/>
                            </svg>
                        </span>
                        <input
                            type="text"
                            className="form-control"
                            placeholder="Search by file name or path..."
                            value={searchQuery}
                            onChange={(e) => setSearchQuery(e.target.value)}
                            onKeyDown={(e) => {
                                if (e.key === 'Escape') {
                                    setSearchQuery("");
                                }
                            }}
                        />
                        {searchQuery !== debouncedSearchQuery && (
                            <span className="input-group-text">
                                <div className="spinner-border spinner-border-sm" role="status" style={{ width: "12px", height: "12px" }}>
                                    <span className="visually-hidden">Searching...</span>
                                </div>
                            </span>
                        )}
                        {searchQuery && (
                            <button
                                className="btn btn-outline-secondary"
                                type="button"
                                onClick={() => setSearchQuery("")}
                                title="Clear search"
                            >
                                ✕
                            </button>
                        )}
                    </div>
                </div>
                <div className="col-md-4">
                    <select
                        className="form-select"
                        value={timeFilter}
                        onChange={(e) => setTimeFilter(e.target.value)}
                    >
                        <option value="< 2 hours">Last 2 hours</option>
                        <option value="< 24 hours">Last 24 hours</option>
                        <option value="< 3 days">Last 3 days</option>
                        <option value="< 7 days">Last 7 days</option>
                    </select>
                </div>
            </div>
            
            {filteredData?.jobRuns.length === 0 ? (
                <Alert variant="info">
                    <Alert.Heading>No Results Found</Alert.Heading>
                    <p>
                        {debouncedSearchQuery ? 
                            `No files matching "${debouncedSearchQuery}" found in the selected time range.` :
                            `No integrity checks found in the selected time range (${timeFilter}).`
                        }
                    </p>
                    {(debouncedSearchQuery || timeFilter !== "< 24 hours") && (
                        <div className="mt-2">
                            <button 
                                className="btn btn-outline-primary btn-sm me-2"
                                onClick={() => setSearchQuery("")}
                            >
                                Clear Search
                            </button>
                            <button 
                                className="btn btn-outline-primary btn-sm"
                                onClick={() => setTimeFilter("< 7 days")}
                            >
                                Show More Results
                            </button>
                        </div>
                    )}
                </Alert>
            ) : (
                <div className="mb-4">
                    <div className="d-flex justify-content-between align-items-center mb-3">
                        <h3>Integrity Check Results by Execution</h3>
                        {(debouncedSearchQuery || filteredData?.jobRuns.length !== liveData?.jobRuns.length) && (
                            <small className="text-muted">
                                Showing {filteredData?.jobRuns.length} of {liveData?.jobRuns.length} executions
                                {debouncedSearchQuery && ` matching "${debouncedSearchQuery}"`}
                            </small>
                        )}
                    </div>
                    <JobRunsList 
                        jobRuns={filteredData.jobRuns} 
                        isCheckRunning={isCheckRunning} 
                        cancelIntegrityCheck={cancelIntegrityCheck}
                        isCancelling={isCancelling}
                        currentRunId={currentRunId}
                    />
                </div>
            )}
        </div>
    );
}

function JobRunsList({ 
    jobRuns, 
    isCheckRunning, 
    cancelIntegrityCheck, 
    isCancelling,
    currentRunId
}: { 
    jobRuns: IntegrityJobRun[]; 
    isCheckRunning?: boolean; 
    cancelIntegrityCheck: () => Promise<void>;
    isCancelling: boolean;
    currentRunId: string | null;
}) {
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

    // Find the run that's most likely active using run ID from WebSocket
    const findActiveRun = () => {
        if (!isCheckRunning || jobRuns.length === 0) {
            console.debug("No active run - isCheckRunning:", isCheckRunning, "jobRuns.length:", jobRuns.length);
            return null;
        }
        
        // If we have a current run ID from WebSocket, ONLY use exact matching
        if (currentRunId) {
            const exactMatch = jobRuns.find(run => run.runId === currentRunId);
            if (exactMatch) {
                console.debug("Found exact run ID match:", currentRunId);
                return exactMatch;
            }
            // If we have a run ID but no match yet, don't show any run as active
            console.debug("Current run ID not found in data yet, showing no active run:", currentRunId);
            return null;
        }
        
        // Only use fallback logic if we don't have a run ID yet (early startup)
        console.debug("No current run ID yet, using fallback detection");
        
        // Fallback: look for a run that has a start time but no end time (actively running)
        for (const run of jobRuns) {
            if (run.startTime && !run.endTime) {
                console.debug("Found active run (no end time):", run.runId, "started:", run.startTime);
                return run; // This is definitely an active run
            }
        }
        
        // Last resort: if WebSocket says check is running but no run ID yet, find the most recent run
        // This should only happen for a very brief moment during startup
        const now = new Date();
        const fiveMinutesAgo = new Date(now.getTime() - 5 * 60 * 1000); // Shorter window to reduce false positives
        
        for (const run of jobRuns) {
            const runTime = run.startTime ? new Date(run.startTime) : new Date(run.date);
            // Only consider very recent runs and only if we don't have a run ID yet
            if (runTime >= fiveMinutesAgo) {
                console.debug("Found recent run as temporary fallback active:", run.runId, "started:", run.startTime || run.date);
                return run;
            }
        }
        
        console.debug("No active run found despite check running. Runs:", jobRuns.map(r => ({ 
            runId: r.runId, 
            startTime: r.startTime, 
            endTime: r.endTime,
            date: r.date 
        })));
        
        return null;
    };

    const activeRun = findActiveRun();

    return (
        <div>
            {jobRuns.map((run, index) => {
                const isActiveRun = isCheckRunning && activeRun && run.runId === activeRun.runId;
                
                return (
                    <Card key={run.date} className={`mb-3 ${isActiveRun ? 'border-primary' : ''}`}>
                        <Card.Header 
                            className={`${isActiveRun ? 'bg-primary bg-opacity-10' : ''} position-relative`}
                            style={{ cursor: 'pointer' }}
                            onClick={() => toggleRun(run.date)}
                        >
                            <div className="d-flex justify-content-between align-items-start">
                                <div className="flex-grow-1">
                                    <div className="d-flex align-items-center mb-1">
                                        <Badge bg="success" className="me-2">
                                            {run.validFiles} valid
                                        </Badge>
                                        {run.corruptFiles > 0 && (
                                            <Badge bg="danger" className="me-2">
                                                {run.corruptFiles} corrupt
                                            </Badge>
                                        )}
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
                                
                                {/* Chevron icon in absolute top right corner */}
                                <div className="position-absolute top-0 end-0 p-3">
                                    <svg 
                                        xmlns="http://www.w3.org/2000/svg" 
                                        width="16" 
                                        height="16" 
                                        fill="currentColor" 
                                        className={`bi bi-chevron-down text-secondary ${expandedRuns.has(run.date) ? 'rotate-180' : ''}`}
                                        style={{ transition: 'transform 0.2s ease' }}
                                        viewBox="0 0 16 16"
                                    >
                                        <path fillRule="evenodd" d="M1.646 4.646a.5.5 0 0 1 .708 0L8 10.293l5.646-5.647a.5.5 0 0 1 .708.708l-6 6a.5.5 0 0 1-.708 0l-6-6a.5.5 0 0 1 0-.708"/>
                                    </svg>
                                </div>
                                
                                {/* Cancel button below main content if active */}
                                {isActiveRun && isCheckRunning && (
                                    <div className="mt-2">
                                        <Button
                                            variant="outline-danger"
                                            size="sm"
                                            onClick={(e) => {
                                                e.stopPropagation(); // Prevent header click
                                                cancelIntegrityCheck();
                                            }}
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
                                    </div>
                                )}
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
