#!/usr/bin/env python3
"""Wrapper to run training inside container without shared import issues."""
import sys
import os


# Mock the shared.logging_utils import
class MockLogging:
    @staticmethod
    def format_training_message(msg, **kw):
        return msg


sys.modules["shared"] = type(sys)("shared")
sys.modules["shared"].logging_utils = MockLogging
sys.modules["shared.logging_utils"] = MockLogging

# Now run the actual training script
script_path = os.path.join(
    os.path.dirname(__file__), "train_intent_classifier_2class.py"
)
with open(script_path) as f:
    code = f.read()
exec(code)
