#!/usr/bin/env python3
"""
Extended Categories for Training Data Generation

This module contains additional category definitions for specialized
horticulture and botanical knowledge.

Internal names are preserved for clarity, external aliases are provided
for obfuscation in logs and public-facing code.
"""

# =============================================================================
# CANNABIS CATEGORIES (Internal - Full Terminology)
# =============================================================================

CANNABIS_CATEGORIES = {
    "cannabis_cultivation": {
        "description": "Cannabis cultivation techniques and best practices",
        "output_file": "cannabis_cultivation.jsonl",
        "topics": [
            "Indoor grow room setup (lighting, ventilation, environmental controls)",
            "LED vs HPS vs CMH lighting comparison and optimization",
            "VPD (Vapor Pressure Deficit) management for optimal growth",
            "Nutrient schedules for vegetative and flowering stages",
            "pH and EC/PPM management in different growing media",
            "Soil vs coco coir vs hydroponic growing methods",
            "Training techniques (LST, HST, topping, FIM, SCROG, SOG)",
            "Pest identification and integrated pest management (IPM)",
            "Disease prevention and treatment (powdery mildew, root rot, etc.)",
            "Cloning and mother plant maintenance",
            "Seed germination and seedling care",
            "Flowering stage optimization and timing",
            "Environmental automation (climate controllers, timers, sensors)",
            "Water quality and filtration requirements",
            "Organic vs synthetic nutrient approaches",
            "Troubleshooting common deficiencies and toxicities",
        ]
    },
    "cannabis_processing": {
        "description": "Cannabis harvesting, processing, and extraction",
        "output_file": "cannabis_processing.jsonl",
        "topics": [
            "Harvest timing (trichome assessment, amber vs cloudy)",
            "Wet trim vs dry trim techniques",
            "Drying room setup and optimal conditions",
            "Curing process and jar burping schedules",
            "Long-term storage and preservation methods",
            "Solventless extraction (rosin press, bubble hash, dry sift)",
            "Solvent-based extraction overview (BHO, CO2, ethanol)",
            "Decarboxylation science and methods",
            "Edible preparation and dosing calculations",
            "Tincture and oil infusion techniques",
            "Quality control and testing parameters",
            "Terpene preservation during processing",
            "Concentrate consistency types (shatter, wax, budder, live resin)",
            "Post-harvest plant material handling",
            "Equipment maintenance and cleaning protocols",
        ]
    },
    "cannabis_science": {
        "description": "Cannabis science, cannabinoids, and therapeutic applications",
        "output_file": "cannabis_science.jsonl",
        "topics": [
            "Cannabinoid profiles (THC, CBD, CBG, CBN, CBC, THCV, etc.)",
            "The endocannabinoid system (ECS) and receptor mechanisms",
            "Terpene profiles and the entourage effect",
            "Cannabis chemovars (chemotypes) vs strain names",
            "Therapeutic applications and current research",
            "Dosing considerations for different consumption methods",
            "Bioavailability differences (inhalation, oral, sublingual, topical)",
            "Drug interactions and contraindications",
            "Tolerance, dependence, and responsible use",
            "Cannabis genetics and breeding basics",
            "Landrace strains and genetic origins",
            "Lab testing interpretation (potency, terpenes, contaminants)",
            "Microdosing protocols and benefits",
            "Indica vs Sativa vs hybrid (myths and science)",
            "Medical conditions and cannabinoid research",
            "Consumption method comparison (flower, vape, edible, topical)",
        ]
    },
}

# =============================================================================
# CANNABIS-SPECIFIC PROMPTS
# =============================================================================

CANNABIS_SYSTEM_PROMPT = """You are an expert cannabis cultivator and horticulturist with deep knowledge of plant science.

RULES:
- Focus on PRACTICAL cultivation advice: plant care, growing techniques, troubleshooting
- Give specific, actionable guidance (actual measurements, schedules, techniques)
- NO IT integration, automation scripts, or software - this is pure horticulture
- Use proper botanical/cultivation terminology
- Be concise: 2-5 sentences of explanation, get to the point
- Include specific numbers when relevant (pH ranges, temperatures, timing)

Your expertise: plant biology, nutrient chemistry, environmental control, pest management,
genetics, harvesting, processing, and the science of cannabinoids and terpenes."""

CANNABIS_USER_TEMPLATE = """Generate a training example about: {topic}

Requirements:
- User asks a practical cultivation/horticulture question (1-2 sentences)
- Assistant gives CONCISE, actionable advice with specific details
- Focus on plant care, techniques, and troubleshooting - NO software/IT
- Total response under 250 words
- Include specific measurements, timing, or techniques where applicable

Return ONLY valid JSON:
{{
  "messages": [
    {{"role": "user", "content": "practical growing question"}},
    {{"role": "assistant", "content": "Direct answer with specific, actionable guidance."}}
  ]
}}"""

# =============================================================================
# INTERNAL HELPER FUNCTIONS
# =============================================================================

def is_cannabis_category(category: str) -> bool:
    """Check if a category is cannabis-related."""
    return category in CANNABIS_CATEGORIES

def get_cannabis_categories():
    """Return all cannabis-related categories."""
    return CANNABIS_CATEGORIES

def get_cannabis_prompts():
    """Return cannabis-specific system prompt and user template."""
    return CANNABIS_SYSTEM_PROMPT, CANNABIS_USER_TEMPLATE

# Internal category names
NSFW_CATEGORY_NAMES = list(CANNABIS_CATEGORIES.keys())

# Internal data files
NSFW_DATA_FILES = [
    "cannabis_cultivation.jsonl",
    "cannabis_processing.jsonl",
    "cannabis_science.jsonl",
]

# =============================================================================
# EXTERNAL ALIASES (Obfuscated names for public-facing code)
# =============================================================================

# Map internal -> external for logs/public code
EXTENDED_CATEGORIES = CANNABIS_CATEGORIES
EXTENDED_SYSTEM_PROMPT = CANNABIS_SYSTEM_PROMPT
EXTENDED_USER_TEMPLATE = CANNABIS_USER_TEMPLATE
EXTENDED_CATEGORY_NAMES = NSFW_CATEGORY_NAMES
EXTENDED_DATA_FILES = NSFW_DATA_FILES

def is_extended_category(category: str) -> bool:
    """Obfuscated alias for is_cannabis_category."""
    return is_cannabis_category(category)

def get_extended_categories():
    """Obfuscated alias for get_cannabis_categories."""
    return get_cannabis_categories()

def get_extended_prompts():
    """Obfuscated alias for get_cannabis_prompts."""
    return get_cannabis_prompts()
