import React from 'react';

/**
 * ErrorBoundary component catches JavaScript errors in child component tree.
 * Prevents entire app from crashing when a single component fails.
 */
class ErrorBoundary extends React.Component {
    constructor(props) {
        super(props);
        this.state = { hasError: false, error: null, errorInfo: null };
    }

    static getDerivedStateFromError(error) {
        return { hasError: true, error };
    }

    componentDidCatch(error, errorInfo) {
        console.error('[ErrorBoundary] Caught error:', error, errorInfo);
        this.setState({ errorInfo });
    }

    handleRetry = () => {
        this.setState({ hasError: false, error: null, errorInfo: null });
    };

    render() {
        if (this.state.hasError) {
            if (this.props.fallback) {
                return this.props.fallback;
            }

            return (
                <div style={{
                    padding: '20px',
                    margin: '10px',
                    borderRadius: '8px',
                    backgroundColor: '#1a1a2e',
                    border: '1px solid #e74c3c',
                    color: '#fff'
                }}>
                    <h3 style={{ color: '#e74c3c', margin: '0 0 10px 0' }}>
                        ⚠️ Something went wrong
                    </h3>
                    <p style={{ color: '#aaa', fontSize: '14px' }}>
                        {this.state.error?.message || 'An unexpected error occurred'}
                    </p>
                    <button
                        onClick={this.handleRetry}
                        style={{
                            marginTop: '10px',
                            padding: '8px 16px',
                            backgroundColor: '#7c3aed',
                            color: '#fff',
                            border: 'none',
                            borderRadius: '6px',
                            cursor: 'pointer',
                            fontSize: '13px'
                        }}
                    >
                        Try Again
                    </button>
                </div>
            );
        }

        return this.props.children;
    }
}

export default ErrorBoundary;
