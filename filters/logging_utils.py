"""
Centralized Logging Utility - Single Source of Truth for ALL Logging

This module provides a unified interface for logging output generation across
the entire Mesosync codebase. All icons, formatting, and status indicators are
generated here - never hardcoded elsewhere.

Usage:
    from layers.shared.logging_utils import log_message, LogLevel, LogCategory
    
    message = log_message("Processing files", LogCategory.FILTER, LogLevel.RUNNING)
    # Returns: "⏳ Processing files"
    
    message = log_message("Task failed", LogCategory.ORCHESTRATOR, LogLevel.ERROR)
    # Returns: "❌ Task failed"

Architecture:
    - LogLevel: Describes the state (SUCCESS, ERROR, WARNING, INFO, RUNNING, etc.)
    - LogCategory: Describes the source (FILTER, ORCHESTRATOR, EXECUTOR, etc.)
    - log_message(): Central function that maps (category, level) -> icon + formatting
    - All icons are defined HERE ONLY - no hardcoding elsewhere
    - All event emitter calls should use this function
"""

from enum import Enum
from typing import Dict, Tuple, Optional


class LogLevel(str, Enum):
    """
    Describes the status/state of a message.
    
    Used to select appropriate icon and styling.
    """
    # Success states
    SUCCESS = "success"          # ✅ Task completed successfully
    DONE = "done"                # ✅ Final completion
    SAVED = "saved"              # 💾 Data persisted
    VERIFIED = "verified"        # ✓ Verification passed
    
    # Running/In-Progress states
    RUNNING = "running"          # ⏳ Task is actively running
    THINKING = "thinking"        # 💭 Model/reasoning in progress
    PROCESSING = "processing"    # ⚡ Processing active
    LOADING = "loading"          # ⏳ Loading model/resource
    SCANNING = "scanning"        # 🔍 Scanning workspace/files
    
    # Information states
    INFO = "info"                # ℹ️ Informational message
    READY = "ready"              # ✅ System ready
    WAITING = "waiting"          # ⏸️ Waiting for user input
    
    # Warning/Problem states
    WARNING = "warning"          # ⚠️ Warning - non-fatal issue
    PARTIAL_ERROR = "partial_error"  # ⚠️ Partial failure (some succeeded)
    
    # Error states
    ERROR = "error"              # ❌ Error occurred
    FAILED = "failed"            # ❌ Task failed
    BLOCKED = "blocked"          # 🚫 Operation blocked
    
    # Data states
    MEMORY = "memory"            # 📚 Memory/semantic search operation
    CONTEXT = "context"          # 📖 Retrieved context
    
    # Tool execution states
    EXECUTING = "executing"      # ⚙️ Tool execution
    RESULT = "result"            # 📊 Tool result/output


class LogCategory(str, Enum):
    """
    Describes the source/component emitting the log message.
    
    Used for routing and context about where message originated.
    """
    # Main components
    FILTER = "filter"                      # AJ Filter (Open-WebUI entry point)
    ORCHESTRATOR = "orchestrator"          # Orchestrator service (reasoning + tool dispatch)
    EXECUTOR = "executor"                  # Executor service (code + shell execution)
    MEMORY = "memory"                      # Memory service (semantic search + storage)
    PRAGMATICS = "pragmatics"             # Pragmatics service (intent classification)
    EXTRACTOR = "extractor"               # Extractor service (document/image processing)
    
    # Sub-components
    DISPATCHER = "dispatcher"              # Tool dispatcher
    REASONING = "reasoning"                # LLM reasoning engine
    FILE_HANDLER = "file_handler"         # File operations
    SHELL_HANDLER = "shell_handler"       # Shell/command execution
    POLYGLOT_HANDLER = "polyglot_handler" # Code execution handler
    STATE = "state"                        # Session state management
    TASK_PLANNER = "task_planner"        # Task planning
    
    # Supporting components
    TRAINING = "training"                 # Model training
    VALIDATION = "validation"             # Validation/checks
    MIGRATION = "migration"               # Data migration


# Icon mapping: (LogCategory, LogLevel) -> icon character
# This is THE ONLY place icons should be defined
ICON_MAP: Dict[Tuple[LogCategory, LogLevel], str] = {
    # SUCCESS icons
    (LogCategory.FILTER, LogLevel.SUCCESS): "✅",
    (LogCategory.FILTER, LogLevel.DONE): "✅",
    (LogCategory.FILTER, LogLevel.SAVED): "💾",
    (LogCategory.ORCHESTRATOR, LogLevel.SUCCESS): "✅",
    (LogCategory.ORCHESTRATOR, LogLevel.DONE): "✅",
    (LogCategory.EXECUTOR, LogLevel.SUCCESS): "✅",
    (LogCategory.MEMORY, LogLevel.SAVED): "💾",
    (LogCategory.PRAGMATICS, LogLevel.SUCCESS): "✅",
    
    # THINKING/PROCESSING icons
    (LogCategory.FILTER, LogLevel.THINKING): "💭",
    (LogCategory.FILTER, LogLevel.RUNNING): "⏳",
    (LogCategory.FILTER, LogLevel.READY): "✅",
    (LogCategory.ORCHESTRATOR, LogLevel.THINKING): "🧠",
    (LogCategory.ORCHESTRATOR, LogLevel.RUNNING): "⚡",
    (LogCategory.REASONING, LogLevel.THINKING): "💭",
    (LogCategory.REASONING, LogLevel.PROCESSING): "🧠",
    (LogCategory.EXECUTOR, LogLevel.RUNNING): "⚡",
    (LogCategory.EXECUTOR, LogLevel.PROCESSING): "⚡",
    (LogCategory.EXECUTOR, LogLevel.EXECUTING): "⚙️",
    (LogCategory.FILE_HANDLER, LogLevel.PROCESSING): "📂",
    (LogCategory.SHELL_HANDLER, LogLevel.EXECUTING): "🔧",
    (LogCategory.POLYGLOT_HANDLER, LogLevel.EXECUTING): "⚙️",
    
    # LOADING icons
    (LogCategory.ORCHESTRATOR, LogLevel.LOADING): "⏳",
    (LogCategory.PRAGMATICS, LogLevel.LOADING): "⏳",
    (LogCategory.REASONING, LogLevel.LOADING): "⏳",
    
    # SCANNING icons
    (LogCategory.FILTER, LogLevel.SCANNING): "🔍",
    (LogCategory.EXECUTOR, LogLevel.SCANNING): "🔍",
    (LogCategory.FILE_HANDLER, LogLevel.SCANNING): "🔍",
    (LogCategory.STATE, LogLevel.SCANNING): "📊",
    
    # MEMORY icons
    (LogCategory.FILTER, LogLevel.MEMORY): "📚",
    (LogCategory.MEMORY, LogLevel.MEMORY): "📚",
    (LogCategory.MEMORY, LogLevel.CONTEXT): "📖",
    (LogCategory.FILTER, LogLevel.CONTEXT): "📖",
    
    # WAITING/INFO icons
    (LogCategory.FILTER, LogLevel.WAITING): "⏸️",
    (LogCategory.FILTER, LogLevel.INFO): "ℹ️",
    (LogCategory.ORCHESTRATOR, LogLevel.INFO): "ℹ️",
    
    # RESULT icons
    (LogCategory.EXECUTOR, LogLevel.RESULT): "📊",
    (LogCategory.FILE_HANDLER, LogLevel.RESULT): "📄",
    (LogCategory.DISPATCHER, LogLevel.RESULT): "📋",
    
    # WARNING icons
    (LogCategory.FILTER, LogLevel.WARNING): "⚠️",
    (LogCategory.FILTER, LogLevel.PARTIAL_ERROR): "⚠️",
    (LogCategory.ORCHESTRATOR, LogLevel.WARNING): "⚠️",
    (LogCategory.EXECUTOR, LogLevel.WARNING): "⚠️",
    (LogCategory.DISPATCHER, LogLevel.WARNING): "⚠️",
    (LogCategory.VALIDATION, LogLevel.WARNING): "⚠️",
    
    # ERROR icons
    (LogCategory.FILTER, LogLevel.ERROR): "❌",
    (LogCategory.FILTER, LogLevel.FAILED): "❌",
    (LogCategory.FILTER, LogLevel.BLOCKED): "🚫",
    (LogCategory.ORCHESTRATOR, LogLevel.ERROR): "❌",
    (LogCategory.ORCHESTRATOR, LogLevel.FAILED): "❌",
    (LogCategory.EXECUTOR, LogLevel.ERROR): "❌",
    (LogCategory.EXECUTOR, LogLevel.FAILED): "❌",
    (LogCategory.MEMORY, LogLevel.ERROR): "❌",
    (LogCategory.DISPATCHER, LogLevel.ERROR): "❌",
    (LogCategory.REASONING, LogLevel.ERROR): "❌",
    (LogCategory.FILE_HANDLER, LogLevel.ERROR): "❌",
    (LogCategory.SHELL_HANDLER, LogLevel.ERROR): "❌",
    (LogCategory.VALIDATION, LogLevel.ERROR): "❌",
    
    # TRAINING icons
    (LogCategory.TRAINING, LogLevel.RUNNING): "🔄",
    (LogCategory.TRAINING, LogLevel.SUCCESS): "✓",
    (LogCategory.TRAINING, LogLevel.ERROR): "❌",
}

# Default fallback for unmapped combinations
DEFAULT_ICON = "◆"


def get_icon(category: LogCategory, level: LogLevel) -> str:
    """
    Get the icon for a given category and level combination.
    
    Args:
        category: The source/component of the message
        level: The status/state of the message
        
    Returns:
        The appropriate icon character (emoji or symbol)
    """
    return ICON_MAP.get((category, level), DEFAULT_ICON)


def log_message(
    message: str,
    category: LogCategory,
    level: LogLevel,
    include_icon: bool = True,
    icon_override: Optional[str] = None,
) -> str:
    """
    Generate a formatted log message with appropriate icon.
    
    This is the SINGLE FUNCTION through which ALL logging happens.
    No icons are hardcoded anywhere else in the codebase.
    
    Args:
        message: The message text (without icon)
        category: The source component (LogCategory enum)
        level: The status/state (LogLevel enum)
        include_icon: Whether to prepend the icon (default True)
        icon_override: Optional custom icon (for special cases)
        
    Returns:
        Formatted message string with icon prefix if include_icon=True
        
    Examples:
        >>> log_message("Processing files", LogCategory.FILTER, LogLevel.RUNNING)
        '⏳ Processing files'
        
        >>> log_message("Task complete", LogCategory.ORCHESTRATOR, LogLevel.SUCCESS)
        '✅ Task complete'
        
        >>> log_message("Error occurred", LogCategory.EXECUTOR, LogLevel.ERROR)
        '❌ Error occurred'
        
        >>> log_message("Custom", LogCategory.FILTER, LogLevel.INFO, icon_override="🎯")
        '🎯 Custom'
    """
    if not include_icon:
        return message
    
    if icon_override:
        icon = icon_override
    else:
        icon = get_icon(category, level)
    
    return f"{icon} {message}"


def create_status_dict(
    message: str,
    category: LogCategory,
    level: LogLevel,
    done: bool = False,
    hidden: bool = False,
) -> Dict[str, any]:
    """
    Create a status dict ready for event emitter.
    
    Simplifies the common pattern of creating status dicts across the codebase.
    
    Args:
        message: Base message text
        category: Source component
        level: Status level
        done: Whether operation is complete
        hidden: Whether to hide in UI
        
    Returns:
        Dict with "type": "status" and formatted "data"
        
    Example:
        >>> await __event_emitter__(create_status_dict("Processing", LogCategory.FILTER, LogLevel.RUNNING))
        # Emits: {"type": "status", "data": {"description": "⏳ Processing", "done": False, "hidden": False}}
    """
    formatted_message = log_message(message, category, level)
    
    return {
        "type": "status",
        "data": {
            "description": formatted_message,
            "done": done,
            "hidden": hidden,
        }
    }


def create_error_dict(
    message: str,
    category: LogCategory,
    done: bool = True,
    hidden: bool = False,
) -> Dict[str, any]:
    """
    Create an error status dict (shorthand).
    
    Args:
        message: Error message
        category: Source component
        done: Whether error marks end of operation
        hidden: Whether to hide in UI
        
    Returns:
        Status dict with ERROR level icon
    """
    return create_status_dict(message, category, LogLevel.ERROR, done=done, hidden=hidden)


def create_success_dict(
    message: str,
    category: LogCategory,
    done: bool = True,
    hidden: bool = False,
) -> Dict[str, any]:
    """
    Create a success status dict (shorthand).
    
    Args:
        message: Success message
        category: Source component
        done: Whether success marks end of operation
        hidden: Whether to hide in UI
        
    Returns:
        Status dict with SUCCESS level icon
    """
    return create_status_dict(message, category, LogLevel.SUCCESS, done=done, hidden=hidden)


# Legacy support for training scripts that use simple print()
# These functions provide drop-in replacements

def format_training_message(text: str, is_success: bool = True) -> str:
    """
    Format a training script message.
    
    Args:
        text: Message text
        is_success: Whether this is a success/completion message
        
    Returns:
        Formatted message for print()
        
    Example:
        >>> print(format_training_message("Training complete"))
        '✓ Training complete'
    """
    level = LogLevel.SUCCESS if is_success else LogLevel.ERROR
    return log_message(text, LogCategory.TRAINING, level)


def format_validation_message(text: str, is_error: bool = False) -> str:
    """
    Format a validation message.
    
    Args:
        text: Message text
        is_error: Whether this indicates an error
        
    Returns:
        Formatted message for print()
    """
    level = LogLevel.ERROR if is_error else LogLevel.SUCCESS
    return log_message(text, LogCategory.VALIDATION, level)
