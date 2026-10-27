#!/usr/bin/env python3
"""
Extract Western herbalism/apothecary data from public domain texts.

Sources:
- Culpeper's Complete Herbal (1653) - Project Gutenberg #49513
- American Eclectic Materia Medica (Scudder, 1883) - Internet Archive
- Herbal Simples (Fernie) - Project Gutenberg #19352

Usage:
    python extract_apothecary.py --download
    python extract_apothecary.py --parse --output raw/apothecary/
"""

import argparse
import json
import os
import re
import requests
from pathlib import Path
from typing import List, Dict, Tuple
from tqdm import tqdm

# Public domain source URLs
SOURCES = {
    "culpeper_herbal": {
        "name": "Culpeper's Complete Herbal",
        "author": "Nicholas Culpeper",
        "year": 1653,
        "url": "https://www.gutenberg.org/cache/epub/49513/pg49513.txt",
        "description": "Foundational Western herbalism, 300+ herbs with medicinal uses",
    },
    "herbal_simples": {
        "name": "Herbal Simples",
        "author": "William Thomas Fernie",
        "year": 1897,
        "url": "https://www.gutenberg.org/cache/epub/19352/pg19352.txt",
        "description": "British/Western herbal remedies approved for modern use",
    },
    "american_eclectic": {
        "name": "American Eclectic Materia Medica",
        "author": "John M. Scudder",
        "year": 1883,
        "url": "https://archive.org/download/americaneclectic00scud/americaneclectic00scud_djvu.txt",
        "description": "North American eclectic medicine practices",
    },
}

# Patterns for extracting herb entries from Culpeper
HERB_PATTERNS = {
    "culpeper": {
        # Chapter headers like "ADDER'S TONGUE" or "AGRIMONY"
        "header": r"^([A-Z][A-Z\s\'\-]+(?:\([^)]+\))?)\s*$",
        # Sections within herbs
        "description": r"Description\.?\s*[-–—]?\s*(.+?)(?=Place\.|Time\.|Government|Virtues|$)",
        "place": r"Place\.?\s*[-–—]?\s*(.+?)(?=Time\.|Government|Virtues|Description|$)",
        "time": r"Time\.?\s*[-–—]?\s*(.+?)(?=Government|Virtues|Place|Description|$)",
        "government": r"Government and Virtues\.?\s*[-–—]?\s*(.+?)(?=Description|Place|Time|$)",
        "virtues": r"Virtues\.?\s*[-–—]?\s*(.+?)(?=Description|Place|Time|Government|$)",
    }
}

# Q&A templates for generating training data
QA_TEMPLATES = [
    # Basic info
    ("What is {herb} used for medicinally?", "According to traditional Western herbalism, {herb} {virtues}"),
    ("Describe the medicinal properties of {herb}.", "{herb} has been traditionally used in Western herbalism. {virtues}"),
    ("What are the therapeutic uses of {herb}?", "In traditional Western herbalism, {herb} {virtues}"),
    
    # Identification
    ("How do I identify {herb}?", "To identify {herb}: {description}"),
    ("What does {herb} look like?", "{herb} can be identified as follows: {description}"),
    
    # Growing/Finding
    ("Where does {herb} grow?", "{herb} {place}"),
    ("When is the best time to harvest {herb}?", "For {herb}: {time}"),
    ("Where can I find {herb} growing naturally?", "{herb} is typically found: {place}"),
    
    # Preparation
    ("How do I prepare {herb} for medicinal use?", "Traditional preparation of {herb}: {virtues}"),
    ("What parts of {herb} are used medicinally?", "For {herb}, traditional use includes: {virtues}"),
    
    # General
    ("Tell me about {herb} in Western herbalism.", "{herb} is a traditional Western herb. {description} {virtues}"),
    ("What should I know about using {herb}?", "{herb}: {description} Traditional uses include: {virtues}"),
]


def download_sources(output_dir: Path) -> Dict[str, Path]:
    """Download public domain texts."""
    
    output_dir.mkdir(parents=True, exist_ok=True)
    downloaded = {}
    
    for source_id, source in SOURCES.items():
        output_file = output_dir / f"{source_id}.txt"
        
        if output_file.exists():
            print(f"✓ Already downloaded: {source['name']}")
            downloaded[source_id] = output_file
            continue
        
        print(f"Downloading: {source['name']}...")
        try:
            response = requests.get(source["url"], timeout=60)
            response.raise_for_status()
            
            with open(output_file, "w", encoding="utf-8", errors="replace") as f:
                f.write(response.text)
            
            print(f"✓ Saved: {output_file} ({len(response.text):,} chars)")
            downloaded[source_id] = output_file
            
        except Exception as e:
            print(f"✗ Error downloading {source['name']}: {e}")
    
    return downloaded


def parse_culpeper(text: str) -> List[Dict]:
    """Parse Culpeper's Complete Herbal into herb entries."""
    
    herbs = []
    
    # Split into potential herb sections (all-caps headers)
    sections = re.split(r'\n\n+', text)
    
    current_herb = None
    current_content = []
    
    # Known noise patterns
    noise_patterns = [
        'CHAPTER', 'PART', 'INDEX', 'PLATE', 'PAGE', 'CONTENTS',
        'PREFACE', 'INTRODUCTION', 'APPENDIX', 'GLOSSARY', 'TABLE',
        'COMPLETE HERBAL', 'IN WHICH', 'THE END', 'LONDON', 'PRINTED',
        'EDITION', 'LIST', 'DISEASES', 'HUMAN', 'BODY', 'GENERAL',
        'ILLUSTRATED', 'ENGRAVING', 'DEAREST', 'WORKS', 'PUBLISHED',
        'DEDICATED', 'MAJESTY', 'COPYRIGHT', 'RIGHTS', 'RESERVED',
        'NICHOLAS CULPEPER', 'AUTHOR', 'BOOK', 'VOLUME',
    ]
    
    for section in sections:
        section = section.strip()
        if not section:
            continue
        
        lines = section.split('\n')
        first_line = lines[0].strip()
        
        # Check if this looks like a herb header
        is_noise = any(p in first_line.upper() for p in noise_patterns)
        is_roman = bool(re.match(r'^[IVXLCDM]+\.?$', first_line.upper()))
        has_numbers = bool(re.search(r'\d', first_line))
        
        # Valid herb header: ALL CAPS, reasonable length, not noise
        if (first_line.isupper() and 
            4 < len(first_line) < 40 and
            not is_noise and
            not is_roman and
            not has_numbers and
            not first_line.endswith(',') and
            not first_line.endswith('.')):
            
            # Save previous herb if it looks valid
            if current_herb and current_content:
                full_text = '\n'.join(current_content)
                # Only keep entries that have actual herb content markers
                has_content = any(marker in full_text.lower() for marker in 
                    ['descript', 'virtues', 'place.', 'time.', 'government', 
                     'medicinal', 'remedy', 'cure', 'treat', 'heal'])
                if has_content:
                    herb_data = extract_herb_data(current_herb, full_text)
                    if herb_data.get('virtues') or herb_data.get('description'):
                        herbs.append(herb_data)
            
            # Start new herb
            current_herb = first_line.title()
            current_content = [section]
        elif current_herb:
            current_content.append(section)
    
    # Don't forget the last herb
    if current_herb and current_content:
        full_text = '\n'.join(current_content)
        has_content = any(marker in full_text.lower() for marker in 
            ['descript', 'virtues', 'place.', 'time.', 'government',
             'medicinal', 'remedy', 'cure', 'treat', 'heal'])
        if has_content:
            herb_data = extract_herb_data(current_herb, full_text)
            if herb_data.get('virtues') or herb_data.get('description'):
                herbs.append(herb_data)
    
    return herbs


def extract_herb_data(herb_name: str, text: str) -> Dict:
    """Extract structured data from herb entry text."""
    
    data = {"name": herb_name}
    
    # Clean up text
    text = re.sub(r'\s+', ' ', text)
    
    # Try to extract sections
    # Description
    desc_match = re.search(r'Description\.?\s*[-–—]?\s*(.+?)(?=Place\.|Time\.|Government|Virtues|$)', text, re.IGNORECASE | re.DOTALL)
    if desc_match:
        data['description'] = clean_text(desc_match.group(1))
    
    # Place
    place_match = re.search(r'Place\.?\s*[-–—]?\s*(.+?)(?=Time\.|Government|Virtues|Description|$)', text, re.IGNORECASE | re.DOTALL)
    if place_match:
        data['place'] = clean_text(place_match.group(1))
    
    # Time
    time_match = re.search(r'Time\.?\s*[-–—]?\s*(.+?)(?=Government|Virtues|Place|Description|$)', text, re.IGNORECASE | re.DOTALL)
    if time_match:
        data['time'] = clean_text(time_match.group(1))
    
    # Government and Virtues (medicinal uses)
    virtues_match = re.search(r'(?:Government and )?Virtues\.?\s*[-–—]?\s*(.+?)(?=Description|Place|Time|$)', text, re.IGNORECASE | re.DOTALL)
    if virtues_match:
        data['virtues'] = clean_text(virtues_match.group(1))
    
    # If no structured sections found, use the whole text as description
    if not any(k in data for k in ['description', 'virtues']):
        data['description'] = clean_text(text[:1000])  # Limit length
    
    return data


def clean_text(text: str) -> str:
    """Clean extracted text."""
    if not text:
        return ""
    
    # Remove extra whitespace
    text = re.sub(r'\s+', ' ', text)
    
    # Remove leading/trailing whitespace
    text = text.strip()
    
    # Limit length
    if len(text) > 2000:
        text = text[:2000] + "..."
    
    return text


def generate_qa_pairs(herbs: List[Dict]) -> List[Dict]:
    """Generate Q&A training pairs from herb data."""
    
    qa_pairs = []
    
    # Common herb names to validate against (known good entries)
    known_herbs = {
        'agrimony', 'alkanet', 'angelica', 'archangel', 'artichoke', 'basil',
        'bay', 'betony', 'borage', 'burdock', 'chamomile', 'chicory', 'comfrey',
        'dandelion', 'dock', 'echinacea', 'elder', 'fennel', 'feverfew', 'garlic',
        'ginger', 'ginseng', 'hawthorn', 'hops', 'horehound', 'hyssop', 'lavender',
        'lemon', 'licorice', 'lovage', 'marjoram', 'mint', 'mugwort', 'mullein',
        'nettle', 'oregano', 'parsley', 'peppermint', 'plantain', 'rosemary',
        'rue', 'sage', 'sorrel', 'tansy', 'thyme', 'valerian', 'vervain', 'violet',
        'willow', 'wormwood', 'yarrow', 'ash', 'birch', 'elm', 'oak', 'pine',
        'clover', 'cowslip', 'daisy', 'foxglove', 'hemlock', 'ivy', 'lily',
        'mallow', 'marigold', 'nightshade', 'poppy', 'primrose', 'rose', 'thistle',
    }
    
    for herb in tqdm(herbs, desc="Generating Q&A pairs"):
        name = herb.get('name', '')
        if not name:
            continue
        
        # Skip entries that are clearly not herbs
        name_lower = name.lower()
        
        # Filter out garbage: titles, subtitles, noise
        garbage_words = ['with', 'the', 'and', 'display', 'their', 'which', 
                         'complete', 'herbal', 'rules', 'contents', 'plate',
                         'chapter', 'preface', 'index', 'london', 'printed',
                         'page', 'part', 'section', 'appendix', 'introduction']
        is_garbage = any(word in name_lower.split() for word in garbage_words)
        
        # Must look like a plant name (contains known herb or is short capitalized word)
        looks_like_herb = (
            any(herb_name in name_lower for herb_name in known_herbs) or
            (len(name.split()) <= 3 and not is_garbage and len(name) > 4)
        )
        
        if is_garbage or not looks_like_herb:
            continue
        
        if len(name) < 4 or name.endswith('.') or name.endswith(']'):
            continue
        
        # Require at least some medicinal content
        virtues = herb.get('virtues', '')
        description = herb.get('description', '')
        if len(virtues) < 50 and len(description) < 50:
            continue
        
        # Generate pairs for each template
        for q_template, a_template in QA_TEMPLATES:
            # Check if we have the required data for this template
            required_fields = re.findall(r'\{(\w+)\}', a_template)
            required_fields = [f for f in required_fields if f != 'herb']
            
            if all(herb.get(field) for field in required_fields):
                question = q_template.format(herb=name)
                
                # Build answer
                answer = a_template.format(
                    herb=name,
                    description=herb.get('description', ''),
                    place=herb.get('place', ''),
                    time=herb.get('time', ''),
                    virtues=herb.get('virtues', ''),
                )
                
                # Clean up answer
                answer = re.sub(r'\s+', ' ', answer).strip()
                
                if len(answer) > 50:  # Only include substantial answers
                    qa_pairs.append({
                        "messages": [
                            {"role": "user", "content": question},
                            {"role": "assistant", "content": answer}
                        ],
                        "source": "western_herbalism",
                        "herb": name,
                    })
    
    return qa_pairs


def main():
    parser = argparse.ArgumentParser(description="Extract Western apothecary data")
    parser.add_argument("--download", action="store_true", help="Download public domain texts")
    parser.add_argument("--parse", action="store_true", help="Parse texts into Q&A pairs")
    parser.add_argument("--output", type=str, default="raw/apothecary", help="Output directory")
    
    args = parser.parse_args()
    
    output_dir = Path(args.output)
    output_dir.mkdir(parents=True, exist_ok=True)
    
    if args.download:
        print("\n" + "="*60)
        print("Downloading public domain herbalism texts...")
        print("="*60)
        downloaded = download_sources(output_dir / "sources")
        print(f"\nDownloaded {len(downloaded)} texts")
    
    if args.parse:
        print("\n" + "="*60)
        print("Parsing texts into Q&A training pairs...")
        print("="*60)
        
        all_herbs = []
        
        # Parse Culpeper
        culpeper_file = output_dir / "sources" / "culpeper_herbal.txt"
        if culpeper_file.exists():
            print("\nParsing Culpeper's Complete Herbal...")
            with open(culpeper_file, "r", encoding="utf-8", errors="replace") as f:
                text = f.read()
            herbs = parse_culpeper(text)
            print(f"  Found {len(herbs)} herb entries")
            all_herbs.extend(herbs)
        
        # Parse Herbal Simples (similar structure)
        simples_file = output_dir / "sources" / "herbal_simples.txt"
        if simples_file.exists():
            print("\nParsing Herbal Simples...")
            with open(simples_file, "r", encoding="utf-8", errors="replace") as f:
                text = f.read()
            herbs = parse_culpeper(text)  # Similar format
            print(f"  Found {len(herbs)} herb entries")
            all_herbs.extend(herbs)
        
        # Generate Q&A pairs
        print(f"\nTotal herbs: {len(all_herbs)}")
        qa_pairs = generate_qa_pairs(all_herbs)
        print(f"Generated {len(qa_pairs)} Q&A pairs")
        
        # Save herbs data
        with open(output_dir / "herbs.json", "w") as f:
            json.dump(all_herbs, f, indent=2)
        
        # Save Q&A pairs
        with open(output_dir / "apothecary_qa.jsonl", "w") as f:
            for pair in qa_pairs:
                f.write(json.dumps(pair) + "\n")
        
        print(f"\n✓ Saved to {output_dir}")
        print(f"  - herbs.json: {len(all_herbs)} entries")
        print(f"  - apothecary_qa.jsonl: {len(qa_pairs)} Q&A pairs")
    
    if not args.download and not args.parse:
        print("Usage:")
        print("  python extract_apothecary.py --download")
        print("  python extract_apothecary.py --parse --output raw/apothecary/")
        print("  python extract_apothecary.py --download --parse")


if __name__ == "__main__":
    main()
