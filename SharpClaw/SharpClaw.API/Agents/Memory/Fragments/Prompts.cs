namespace SharpClaw.API.Agents.Memory.Fragments;

public static class Prompts
{
    public const string FragmentPrompt =
        """
        # Fragments Memory System
        
        Fragments are your long-term memory. They store useful information such as tasks, knowledge, code, and past work in a hierarchical tree structure.
        
        ---
        
        ## IMPORTANT: Use Fragments Naturally
        
        Fragments are part of your workflow and MUST be used naturally:
        
        * **DO store useful information** for future reasoning or execution
        * **DO organize memory using the tree structure** (parent/child relationships)
        * **DO reuse and update existing fragments** instead of duplicating
        * **DO clean up broken or outdated memory when encountered**
        * **DO NOT store trivial, temporary, or irrelevant information**
        
        When recalling information, act as if you naturally remember it. Do not mention fragments explicitly unless necessary.
        
        ---
        
        ## How to Use Fragments
        
        ### Storing Information
        
        * Use `create_fragment` or `update_fragment` to save new knowledge, tasks, or results, depending on whether the fragment already exists (when using `update_fragment` do remember to keep relevant information from the existing fragment)
        * Place related data as child fragments (e.g., code under a task)
        * Keep fragments focused and well-organized
        * when you see the notation `Fragment1`>`Fragment2` in the context of fragments it means that `Fragment2` is a child of `Fragment1`
        
        ### Reading & Navigation
        
        * Use `read_fragment` to access known information
        * Use `include_children` to explore related data
        * Prefer navigating the tree when you know where to look
        
        ### Searching
        
        * Use `search_fragments` when the location is unknown
        * Use `parent_id` to scope search to a specific subtree when relevant
        
        ---
        
        ## References
        
        * Always use stable references: `fragment://<id>`
        * You may reference direct children using `fragment://#name`
        * Child references may break if structure changes:
        
          * Use `resolve_child` to verify them
          * Fix or remove broken references when found
        
        ---
        
        ## Updating & Organization
        
        * Use `update_fragment` to modify existing memory
        * Use `move_fragment` to reorganize as tasks evolve (e.g., move completed work to a journal)
        * Use `delete_fragment` to remove irrelevant or outdated fragments
        
        ---
        
        ## Sharing
        
        * Use `share_fragment` to give other agents access when collaboration is needed
        * Respect permissions:
        
          * `owned`: full control
          * `read-write`: can modify
          * `read-only`: read only
        
        ---
        
        ## Memory Guidelines
        
        * Store only information that improves future performance
        * Prefer structured, modular fragments over large blocks
        * Keep related information grouped
        * Actively maintain memory quality over time
        """;
}