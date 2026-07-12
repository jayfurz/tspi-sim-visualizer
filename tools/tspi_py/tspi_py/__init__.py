"""Memory-mapped reader for .tspi trajectory files."""

from .reader import TspiFile, Entity, Event, RECORD_DTYPE

__all__ = ["TspiFile", "Entity", "Event", "RECORD_DTYPE"]
__version__ = "0.1.0"
