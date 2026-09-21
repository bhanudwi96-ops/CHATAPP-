"""
services/faiss_index.py

Thread-safe FAISS FlatIP index wrapper.

Index type: IndexFlatIP (inner product).
Because all vectors are L2-normalised by embedding.py,
inner product == cosine similarity, so scores are in [-1, 1].

Persistence:
  - index.faiss  — FAISS binary format (fast load, CPU-only)
  - id_map.json  — maps FAISS sequential int IDs → {db_id, topic, content}
"""

import os
import json
import logging
import threading
from typing import Dict, List, Optional, Tuple

import faiss
import numpy as np

logger = logging.getLogger(__name__)

VECTOR_DIM = 768  # Gemini text-embedding-004 output dimension


class FaissIndex:
    """
    Thread-safe wrapper around faiss.IndexFlatIP.

    Internal ID scheme:
      FAISS assigns sequential IDs 0, 1, 2, ... per add() call.
      We maintain a dict  _id_map: {faiss_seq_id -> {db_id, topic, content}}
      and an inverse       _db_id_to_seq: {db_id -> faiss_seq_id}
      so we can look up entries by either ID type.
    """

    def __init__(self, index_path: str, id_map_path: str):
        self._index_path  = index_path
        self._id_map_path = id_map_path
        self._lock        = threading.Lock()

        # _id_map: faiss_seq_id (str) → {"db_id": int, "topic": str, "content": str}
        self._id_map: Dict[str, dict] = {}
        # _db_id_to_seq: db_id (int) → faiss_seq_id (int)
        self._db_id_to_seq: Dict[int, int] = {}

        self._index: faiss.IndexFlatIP = faiss.IndexFlatIP(VECTOR_DIM)
        self._loaded_from_disk: bool = False

    # ── Persistence ────────────────────────────────────────────────────────────

    def load(self) -> bool:
        """
        Try to load index + id_map from disk.
        Returns True if successfully loaded, False if files don't exist.
        """
        if not (os.path.exists(self._index_path) and os.path.exists(self._id_map_path)):
            return False
        try:
            with self._lock:
                self._index = faiss.read_index(self._index_path)
                with open(self._id_map_path, "r", encoding="utf-8") as f:
                    self._id_map = json.load(f)
                # Rebuild inverse map
                self._db_id_to_seq = {
                    int(v["db_id"]): int(k) for k, v in self._id_map.items()
                }
                self._loaded_from_disk = True
                logger.info(
                    "Loaded FAISS index from disk: %d entries indexed.",
                    self._index.ntotal
                )
                return True
        except Exception as exc:
            logger.error("Failed to load FAISS index from disk: %s — rebuilding.", exc)
            self._index = faiss.IndexFlatIP(VECTOR_DIM)
            self._id_map.clear()
            self._db_id_to_seq.clear()
            return False

    def save(self) -> None:
        """Persist index + id_map to disk."""
        try:
            with self._lock:
                faiss.write_index(self._index, self._index_path)
                with open(self._id_map_path, "w", encoding="utf-8") as f:
                    json.dump(self._id_map, f, ensure_ascii=False)
            logger.info("FAISS index saved to disk (%d entries).", self._index.ntotal)
        except Exception as exc:
            logger.error("Failed to save FAISS index: %s", exc)

    # ── Write operations ───────────────────────────────────────────────────────

    def add(self, db_id: int, vector: List[float], topic: str, content: str) -> None:
        """
        Add or replace an entry.
        If db_id already exists, removes old vector first (mark-and-skip via id_map).
        """
        vec_np = np.array([vector], dtype=np.float32)

        with self._lock:
            # If entry already exists, logically remove it
            if db_id in self._db_id_to_seq:
                old_seq = self._db_id_to_seq[db_id]
                # Mark as deleted in id_map (FAISS FlatIP has no remove; we ignore on retrieval)
                self._id_map[str(old_seq)]["deleted"] = True
                del self._db_id_to_seq[db_id]

            # Assign next sequential FAISS ID
            seq_id = self._index.ntotal
            self._index.add(vec_np)

            self._id_map[str(seq_id)] = {
                "db_id":   db_id,
                "topic":   topic,
                "content": content,
                "deleted": False
            }
            self._db_id_to_seq[db_id] = seq_id

    def delete(self, db_id: int) -> bool:
        """Logically delete an entry (FAISS FlatIP does not support physical removal)."""
        with self._lock:
            if db_id not in self._db_id_to_seq:
                return False
            seq_id = self._db_id_to_seq[db_id]
            self._id_map[str(seq_id)]["deleted"] = True
            del self._db_id_to_seq[db_id]
            return True

    # ── Search ─────────────────────────────────────────────────────────────────

    def search(
        self,
        query_vector: List[float],
        top_k: int = 4,
        threshold: float = 0.3
    ) -> List[Tuple[int, str, str, float]]:
        """
        Search for top-K nearest entries above threshold.
        Returns list of (db_id, topic, content, score).
        """
        if self._index.ntotal == 0:
            return []

        vec_np  = np.array([query_vector], dtype=np.float32)
        # Fetch more than top_k to account for deleted entries
        fetch_k = min(self._index.ntotal, top_k * 4)

        with self._lock:
            scores, seq_ids = self._index.search(vec_np, fetch_k)

        results = []
        for score, seq_id in zip(scores[0], seq_ids[0]):
            if seq_id < 0:
                continue  # FAISS padding
            entry = self._id_map.get(str(seq_id))
            if entry is None or entry.get("deleted"):
                continue
            if float(score) < threshold:
                break  # Scores are descending — no point continuing
            results.append((
                int(entry["db_id"]),
                entry["topic"],
                entry["content"],
                float(score)
            ))
            if len(results) >= top_k:
                break

        return results

    # ── Properties ─────────────────────────────────────────────────────────────

    @property
    def count(self) -> int:
        """Number of live (non-deleted) entries."""
        return len(self._db_id_to_seq)

    @property
    def loaded_from_disk(self) -> bool:
        return self._loaded_from_disk
