"""
services/db_loader.py

Reads all rows from the KnowledgeBase table in SQL Server.
Uses Windows Authentication (Trusted_Connection=yes) via pyodbc — no username/password needed.
Returns a list of dicts: [{id, topic, content}, ...]
"""

import os
import logging
from typing import List, Dict, Any

import pyodbc

logger = logging.getLogger(__name__)

# Default connection params — overridden by env vars
_DEFAULT_SERVER   = r"(localdb)\mssqllocaldb"
_DEFAULT_DATABASE = "ChatAppDb"
_DEFAULT_DRIVER   = "{ODBC Driver 17 for SQL Server}"


def load_all_kb_entries() -> List[Dict[str, Any]]:
    """
    Load all rows from KnowledgeBase table.
    Returns list of {id: int, topic: str, content: str}.
    Raises RuntimeError if the DB is unreachable.
    """
    driver   = os.getenv("DB_DRIVER", _DEFAULT_DRIVER)
    server   = os.getenv("DB_SERVER", _DEFAULT_SERVER)
    database = os.getenv("DB_DATABASE", _DEFAULT_DATABASE)
    conn_str = os.getenv(
        "DB_CONNECTION_STRING",
        f"Driver={driver};Server={server};Database={database};Trusted_Connection=yes;"
    )
    entries: List[Dict[str, Any]] = []

    try:
        logger.info("Connecting to SQL Server via pyodbc (Trusted_Connection=yes)...")
        with pyodbc.connect(conn_str, timeout=10) as conn:
            cursor = conn.cursor()
            cursor.execute("SELECT Id, Topic, Content FROM KnowledgeBase ORDER BY Id")
            rows = cursor.fetchall()
            for row in rows:
                entries.append({
                    "id":      int(row[0]),
                    "topic":   str(row[1]),
                    "content": str(row[2])
                })
        logger.info("Loaded %d KnowledgeBase entries from SQL Server.", len(entries))
        return entries

    except Exception as exc:
        logger.error("Failed to connect to SQL Server: %s", exc)
        raise RuntimeError(f"Database connection failed: {exc}") from exc
