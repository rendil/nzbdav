import type { Route } from "./+types/route";
import styles from "./route.module.css";
import { useState, useEffect } from "react";
import { Card, Table, Badge, Alert, Button, Collapse } from "react-bootstrap";

type IntegrityFileResult = {
    fileId: string;
    filePath: string;
    fileName: string;
    isLibraryFile: boolean;
    lastChecked: string;
    status: string;
    errorMessage?: string;
    actionTaken?: string;
};

type IntegrityJobRun = {
    date: string;
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

    if (!data) {
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
            <h1>Media Integrity Results</h1>
            
            {data.jobRuns.length === 0 ? (
                <Alert variant="info">
                    <Alert.Heading>No Integrity Checks Found</Alert.Heading>
                    <p>No integrity checks have been completed yet. Check your settings to enable integrity checking.</p>
                </Alert>
            ) : (
                <div className="mb-4">
                    <h3>Integrity Check Results by Date</h3>
                    <JobRunsList jobRuns={data.jobRuns} />
                </div>
            )}
        </div>
    );
}

function JobRunsList({ jobRuns }: { jobRuns: IntegrityJobRun[] }) {
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

    return (
        <div>
            {jobRuns.map((run) => (
                <Card key={run.date} className="mb-3">
                    <Card.Header>
                        <div className="d-flex justify-content-between align-items-center">
                            <div>
                                <strong>{new Date(run.date).toLocaleDateString()}</strong>
                                <span className="ms-3">
                                    {run.totalFiles} files checked
                                </span>
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
                                <Button
                                    variant="outline-secondary"
                                    size="sm"
                                    onClick={() => toggleRun(run.date)}
                                >
                                    {expandedRuns.has(run.date) ? "Hide" : "Show"} Files
                                </Button>
                            </div>
                        </div>
                    </Card.Header>
                    <Collapse in={expandedRuns.has(run.date)}>
                        <Card.Body>
                            <FilesTable files={run.files} />
                        </Card.Body>
                    </Collapse>
                </Card>
            ))}
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
                            {new Date(file.lastChecked).toLocaleString()}
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
