"""
Training examples for 2-class intent classifier.

Classes:
  - CASUAL: Greetings, chitchat, questions, explanations (no agent action needed)
  - TASK: Workspace operations, code execution, agent queries (needs action)

Note: SAVE and RECALL are kept for legacy 4-class compatibility but map to:
  - SAVE → TASK (storing info needs action)
  - RECALL → CASUAL (asking for info is conversation)
"""

# =============================================================================
# CASUAL EXAMPLES - No agent action required
# =============================================================================
CASUAL_EXAMPLES = [
    # Greetings
    "Hello",
    "Hi there",
    "Hey",
    "Good morning",
    "Good afternoon",
    "Hi AJ",
    "Hey AJ",
    "Hello AJ",
    "What's up?",
    "Howdy",
    # General questions
    "What is Python?",
    "How does async/await work?",
    "Explain the difference between let and const",
    "What's the difference between a class and a function?",
    "Can you explain REST APIs?",
    "What is Docker?",
    "How do containers work?",
    "What's the difference between SQL and NoSQL?",
    "Explain microservices architecture",
    "What is Kubernetes?",
    # Clarifications
    "Can you explain that again?",
    "What do you mean?",
    "Could you be more specific?",
    "I don't understand",
    "Can you give an example?",
    "Tell me more about that",
    # Opinions/preferences
    "Which framework is better?",
    "Should I use React or Vue?",
    "What's your recommendation?",
    "What do you think about TypeScript?",
    "Is Python good for beginners?",
    # Chitchat
    "Thanks!",
    "Thank you",
    "That's helpful",
    "Great!",
    "Awesome",
    "Nice",
    "OK",
    "Sounds good",
    "Perfect",
    "Got it",
    # Meta questions
    "Who are you?",
    "What can you do?",
    "Who made you?",
    "What's your name?",
    "Are you an AI?",
]

# =============================================================================
# SAVE EXAMPLES - User providing info to remember
# =============================================================================
SAVE_EXAMPLES = [
    # Explicit save requests
    "Remember that my name is Ian",
    "Save this: the API key is abc123",
    "Note that the server IP is 192.168.1.100",
    "Keep in mind that I prefer dark mode",
    "Store this password for later",
    "Remember I like TypeScript",
    # Implicit save context
    "My email is ian@example.com",
    "I work at Acme Corp",
    "The production server is at prod.example.com",
    "My preferred editor is VS Code",
    "I usually deploy on Fridays",
    "The database password is secret123",
    # Preferences
    "I prefer tabs over spaces",
    "Use 2-space indentation",
    "I like functional programming",
    "Always use strict mode",
    "I want verbose logging",
]

# =============================================================================
# RECALL EXAMPLES - User asking for remembered info
# =============================================================================
RECALL_EXAMPLES = [
    # Direct recall
    "What's my name?",
    "What was that API key?",
    "What's the server IP again?",
    "Remind me of the password",
    "What email did I give you?",
    # Context recall
    "What do you know about me?",
    "What have I told you?",
    "What's in your memory?",
    "What facts do you remember?",
    "Tell me what you know",
    # Preferences recall
    "What's my preferred editor?",
    "Do I like tabs or spaces?",
    "What's my indentation setting?",
    "What company do I work for?",
]

# =============================================================================
# TASK EXAMPLES - Requires agent execution
# =============================================================================
TASK_EXAMPLES = [
    # File operations
    "Create a new Python file",
    "Write a function to sort this list",
    "Delete the temp folder",
    "Read the config file",
    "Update the README",
    "Move this file to the archive",
    "Copy the logs to backup",
    "Create a new directory called src",
    "Rename main.py to app.py",
    # Code execution
    "Run the tests",
    "Execute this Python script",
    "Build the Docker container",
    "Run npm install",
    "Start the dev server",
    "Compile the TypeScript",
    "Run the linter",
    "Execute make build",
    # Deployment
    "Deploy to production",
    "Push to main branch",
    "Create a pull request",
    "Merge the feature branch",
    "Roll back the deployment",
    "Tag the release",
    # Git operations
    "Commit these changes",
    "Show git status",
    "Create a new branch",
    "Pull latest changes",
    "Stash my changes",
    "Show the git log",
    # Configuration
    "Update the config file",
    "Set the environment variable",
    "Configure eslint",
    "Install the dependencies",
    "Add a new route handler",
    # Remote/Agent operations
    "Connect to the remote server",
    "List files on my Windows PC",
    "Check disk space on the agent",
    "Run Get-Process on dev-workstation",
    "Find large files on S: drive",
    "Execute this PowerShell script",
    "Restart the service on remote machine",
    "Copy files from remote to local",
    "Monitor CPU usage on the agent",
    # =========================================================================
    # AGENT DISCOVERY AND STATUS QUERIES - These are TASKS, not casual!
    # =========================================================================
    # Direct agent queries
    "How many agents are online?",
    "How many agents are available?",
    "How many FunnelCloud agents are running?",
    "What agents are online?",
    "Which agents are available?",
    "List the online agents",
    "List all agents",
    "Show me the agents",
    "Show available agents",
    "Are any agents online?",
    "Are there any agents available?",
    "Is my agent running?",
    "Check if agents are online",
    "Discover agents",
    "Find available agents",
    "Discover FunnelCloud agents",
    "Find FunnelCloud agents on the network",
    # Agent status with greetings
    "Hey, how many agents are online?",
    "Hey AJ, how many agents are online?",
    "Hi, what agents are available?",
    "Hello, list the online agents",
    "Hi AJ, show me the agents",
    "Hey, are any agents online?",
    "Hey AJ, discover agents",
    # Variations
    "What machines are connected?",
    "What computers are online?",
    "How many machines do we have?",
    "What's the agent count?",
    "Agent status please",
    "Give me agent status",
    "Check agent connectivity",
    "Verify agents are reachable",
    # Agent-specific execution
    "Run a command on all agents",
    "Execute on the Windows agent",
    "Run PowerShell on ians-r16",
    "Check memory on all machines",
    "Get system info from agents",
    "Ping all agents",
    "Health check all agents",
    # Network discovery
    "Scan for agents",
    "What's on the network?",
    "Network discovery",
    "Multicast discovery",
    "Find peers",
    "Discover peers",
    # Database operations
    "Query the database",
    "Run this SQL",
    "Create a new table",
    "Insert this record",
    "Update the user table",
    "Delete old records",
    # Debugging
    "Debug this function",
    "Set a breakpoint",
    "Show the call stack",
    "Print the variable values",
    "Trace the execution",
    # Analysis
    "Analyze the code",
    "Find security issues",
    "Check for memory leaks",
    "Profile the performance",
    "Generate a report",
    "Scan the workspace",
    "Search for TODO comments",
]
