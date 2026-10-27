"""
title: Capture Training Data
author: Agentic
version: 0.4.0
required_open_webui_version: 0.4.0
icon_url: data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAyNCAyNCIgZmlsbD0iY3VycmVudENvbG9yIj48cGF0aCBkPSJNMTIgMkM2LjQ4IDIgMiA2LjQ4IDIgMTJzNC40OCAxMCAxMCAxMCAxMC00LjQ4IDEwLTEwUzE3LjUyIDIgMTIgMnptLTEgMTdIOVYxM2gydjZoLTJ2LTZ6bTQgMGgtMnYtNGgydjR6bTAtNmgtMlY5aDJ2NHoiLz48L3N2Zz4=

Capture Training Data Action for AJ.

Allows users to rate and flag conversations for training data extraction.
Uses embedded HTML form with dropdowns and checkboxes for better UX.
Form submits directly to orchestrator API - no popup modal.
Stores to Qdrant for later extraction into training datasets.
"""

import os
import json
import httpx
from typing import Optional
from pydantic import BaseModel, Field
from datetime import datetime


# Orchestrator URL
ORCHESTRATOR_API_URL = os.getenv("ORCHESTRATOR_API_URL", "http://orchestrator_api:8004")


def generate_form_html(session_id: str, message_id: str, user_prompt: str, model_response: str, model_id: str, user_id: str, orchestrator_url: str) -> str:
    """Generate the HTML form for training data capture with embedded context."""
    # Escape strings for safe JSON embedding
    import html
    context_json = json.dumps({
        "session_id": session_id,
        "message_id": message_id,
        "user_prompt": user_prompt,
        "model_response": model_response,
        "model_id": model_id,
        "user_id": user_id,
    })
    
    return f'''<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <style>
        * {{ box-sizing: border-box; margin: 0; padding: 0; }}
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            background: transparent;
            color: #e5e7eb;
            padding: 16px;
        }}
        .form-group {{ margin-bottom: 16px; }}
        label {{ display: block; margin-bottom: 6px; font-weight: 500; font-size: 14px; }}
        select {{
            width: 100%;
            padding: 10px 12px;
            border: 1px solid #374151;
            border-radius: 8px;
            background: #1f2937;
            color: #e5e7eb;
            font-size: 14px;
            cursor: pointer;
        }}
        select:focus {{ outline: none; border-color: #6366f1; }}
        .checkbox-group {{ display: flex; flex-wrap: wrap; gap: 8px; }}
        .checkbox-item {{
            display: inline-flex;
            align-items: center;
            gap: 6px;
            padding: 6px 12px;
            border: 1px solid #374151;
            border-radius: 6px;
            background: #1f2937;
            cursor: pointer;
            font-size: 13px;
            transition: all 0.15s;
            user-select: none;
        }}
        .checkbox-item:hover {{ border-color: #6366f1; background: #374151; }}
        .checkbox-item.selected {{ 
            border-color: #6366f1;
            background: #312e81;
        }}
        input[type="text"] {{
            width: 100%;
            padding: 10px 12px;
            border: 1px solid #374151;
            border-radius: 8px;
            background: #1f2937;
            color: #e5e7eb;
            font-size: 14px;
        }}
        input[type="text"]:focus {{ outline: none; border-color: #6366f1; }}
        .button-row {{ display: flex; gap: 12px; margin-top: 20px; }}
        button {{
            flex: 1;
            padding: 12px 20px;
            border: none;
            border-radius: 8px;
            font-size: 14px;
            font-weight: 500;
            cursor: pointer;
            transition: all 0.15s;
        }}
        .btn-primary {{ background: #6366f1; color: white; }}
        .btn-primary:hover {{ background: #4f46e5; }}
        .btn-primary:disabled {{ background: #4b5563; cursor: not-allowed; }}
        .btn-secondary {{ background: #374151; color: #e5e7eb; }}
        .btn-secondary:hover {{ background: #4b5563; }}
        .rating-stars {{ display: flex; gap: 4px; }}
        .star {{
            font-size: 28px;
            cursor: pointer;
            transition: transform 0.1s;
            color: #374151;
        }}
        .star:hover {{ transform: scale(1.2); }}
        .star.active {{ color: #fbbf24; }}
        .status {{ 
            text-align: center; 
            padding: 8px; 
            border-radius: 6px;
            margin-top: 12px;
            display: none;
        }}
        .status.success {{ display: block; background: #065f46; color: #6ee7b7; }}
        .status.error {{ display: block; background: #7f1d1d; color: #fca5a5; }}
        .status.saving {{ display: block; background: #1e3a5f; color: #93c5fd; }}
    </style>
</head>
<body>
    <div id="captureForm">
        <div class="form-group">
            <label>⭐ Rating (click to rate)</label>
            <div class="rating-stars" id="ratingStars">
                <span class="star" data-value="1">★</span>
                <span class="star" data-value="2">★</span>
                <span class="star" data-value="3">★</span>
                <span class="star" data-value="4">★</span>
                <span class="star" data-value="5">★</span>
            </div>
        </div>
        
        <div class="form-group">
            <label>📁 Training Type</label>
            <select id="trainingType">
                <option value="good_example">✅ Good Example - Worth emulating</option>
                <option value="edge_case">🔍 Edge Case - Unusual but correct</option>
                <option value="hard_negative">❌ Hard Negative - Example of what NOT to do</option>
                <option value="knowledge_graph">📚 Knowledge - Facts/info to remember</option>
                <option value="feedback">💬 Feedback - General feedback</option>
            </select>
        </div>
        
        <div class="form-group">
            <label>🏷️ Quick Tags (click to toggle)</label>
            <div class="checkbox-group" id="tagCheckboxes">
                <span class="checkbox-item" data-tag="code">💻 Code</span>
                <span class="checkbox-item" data-tag="reasoning">🧠 Reasoning</span>
                <span class="checkbox-item" data-tag="creative">🎨 Creative</span>
                <span class="checkbox-item" data-tag="factual">📖 Factual</span>
                <span class="checkbox-item" data-tag="tool-use">🔧 Tool Use</span>
                <span class="checkbox-item" data-tag="format">📋 Format</span>
            </div>
        </div>
        
        <div class="form-group">
            <label>🏷️ Custom Tags (comma-separated)</label>
            <input type="text" id="customTags" placeholder="e.g., python, async, error-handling">
        </div>
        
        <div class="button-row">
            <button type="button" class="btn-secondary" id="cancelBtn">Cancel</button>
            <button type="button" class="btn-primary" id="saveBtn">💾 Save</button>
        </div>
        
        <div id="status" class="status"></div>
    </div>
    
    <script>
        // Context passed from action
        const context = {context_json};
        const orchestratorUrl = '{orchestrator_url}';
        
        // Star rating - default to 0 (none selected)
        let currentRating = 0;
        const stars = document.querySelectorAll('.star');
        
        function updateStars(rating) {{
            stars.forEach((star, idx) => {{
                star.classList.toggle('active', idx < rating);
            }});
            currentRating = rating;
        }}
        
        stars.forEach(star => {{
            star.addEventListener('click', () => {{
                const val = parseInt(star.dataset.value);
                // If clicking same star, toggle off
                if (val === currentRating) {{
                    updateStars(0);
                }} else {{
                    updateStars(val);
                }}
            }});
            star.addEventListener('mouseenter', () => {{
                const val = parseInt(star.dataset.value);
                stars.forEach((s, idx) => s.classList.toggle('active', idx < val));
            }});
        }});
        
        document.getElementById('ratingStars').addEventListener('mouseleave', () => {{
            updateStars(currentRating);
        }});
        
        // Checkbox/tag toggle - using data attributes instead of hidden inputs
        const tagItems = document.querySelectorAll('.checkbox-item');
        tagItems.forEach(item => {{
            item.addEventListener('click', () => {{
                item.classList.toggle('selected');
            }});
        }});
        
        // Get selected tags
        function getSelectedTags() {{
            const selected = [];
            tagItems.forEach(item => {{
                if (item.classList.contains('selected')) {{
                    selected.push(item.dataset.tag);
                }}
            }});
            const customTags = document.getElementById('customTags').value
                .split(',')
                .map(t => t.trim().toLowerCase())
                .filter(t => t);
            return [...selected, ...customTags];
        }}
        
        // Show status
        function showStatus(message, type) {{
            const status = document.getElementById('status');
            status.className = 'status ' + type;
            status.textContent = message;
        }}
        
        // Cancel
        document.getElementById('cancelBtn').addEventListener('click', () => {{
            // Just collapse/hide the form
            document.getElementById('captureForm').innerHTML = '<div style="text-align:center;color:#9ca3af;padding:20px;">❌ Cancelled</div>';
        }});
        
        // Save - submit directly to orchestrator
        document.getElementById('saveBtn').addEventListener('click', async () => {{
            const rating = currentRating;
            const trainingType = document.getElementById('trainingType').value;
            const tags = getSelectedTags();
            
            // Validate
            if (rating === 0) {{
                showStatus('Please select a rating (1-5 stars)', 'error');
                return;
            }}
            
            showStatus('💾 Saving...', 'saving');
            document.getElementById('saveBtn').disabled = true;
            
            const payload = {{
                ...context,
                timestamp: new Date().toISOString(),
                rating: rating,
                training_type: trainingType,
                tags: tags
            }};
            
            try {{
                const resp = await fetch(orchestratorUrl + '/api/training/capture', {{
                    method: 'POST',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify(payload)
                }});
                
                if (resp.ok) {{
                    showStatus('✅ Saved as ' + trainingType + ' (rating: ' + rating + '/5)', 'success');
                    // Collapse form after success
                    setTimeout(() => {{
                        document.getElementById('captureForm').innerHTML = '<div style="text-align:center;color:#6ee7b7;padding:20px;">✅ Captured!</div>';
                    }}, 1500);
                }} else {{
                    const err = await resp.text();
                    showStatus('❌ Failed: ' + resp.status, 'error');
                    document.getElementById('saveBtn').disabled = false;
                }}
            }} catch (e) {{
                showStatus('❌ Error: ' + e.message, 'error');
                document.getElementById('saveBtn').disabled = false;
            }}
        }});
        
        // Tell parent about height
        window.parent.postMessage({{ type: 'iframe:height', height: document.body.scrollHeight + 40 }}, '*');
    </script>
</body>
</html>'''


class Action:
    """
    Training Data Capture Action.
    
    Adds a button below messages to rate and flag conversations
    for training data extraction. Form submits directly to orchestrator API.
    """
    
    class Valves(BaseModel):
        """Configuration for the training capture action."""
        orchestrator_url: str = Field(
            default="http://orchestrator_api:8004",
            description="URL of the orchestrator API (server-side, Docker internal)"
        )
        browser_orchestrator_url: str = Field(
            default="http://localhost:8004",
            description="URL of the orchestrator API (client-side, browser accessible)"
        )
        enable_session_capture: bool = Field(
            default=True,
            description="Include session state in captures"
        )
    
    def __init__(self):
        self.valves = self.Valves()
    
    async def action(
        self,
        body: dict,
        __user__: Optional[dict] = None,
        __event_emitter__=None,
        __event_call__=None,
        __model__: Optional[dict] = None,
    ) -> dict:
        """
        Main action handler - shows training data capture form.
        
        Form submits directly to orchestrator API via fetch - no popup needed.
        """
        # Get the message content
        message_content = body.get("content", "")
        message_id = body.get("id", "")
        
        # Get conversation history to find the user prompt
        messages = body.get("messages", [])
        
        # Find the user prompt that preceded this response
        user_prompt = ""
        for i, msg in enumerate(messages):
            if msg.get("id") == message_id:
                # Look backwards for the user message
                for j in range(i - 1, -1, -1):
                    if messages[j].get("role") == "user":
                        user_prompt = messages[j].get("content", "")
                        break
                break
        
        # If we couldn't find it in messages, try to get from body
        if not user_prompt:
            user_prompt = body.get("user_message", "")
        
        if not __event_emitter__:
            return {"content": ""}
        
        # Get IDs for the capture
        session_id = body.get("session_id", body.get("chat_id", ""))
        model_id = __model__.get("id", "") if __model__ else ""
        user_id = __user__.get("id", "") if __user__ else ""
        
        # Generate form with embedded context
        # Use browser_orchestrator_url since form runs client-side in the browser
        form_html = generate_form_html(
            session_id=session_id,
            message_id=message_id,
            user_prompt=user_prompt,
            model_response=message_content,
            model_id=model_id,
            user_id=user_id,
            orchestrator_url=self.valves.browser_orchestrator_url
        )
        
        # Emit the HTML form as an embed - no popup, form handles everything
        await __event_emitter__({
            "type": "embeds",
            "data": {
                "embeds": [form_html]
            }
        })
        
        # Show brief status (form handles the rest)
        await __event_emitter__({
            "type": "status",
            "data": {"description": "📊 Rate and tag this response", "done": True}
        })
        
        return {"content": ""}
