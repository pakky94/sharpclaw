## Most important stuff:

### Fragments improvements
- [X] Add root fragments to `Environment.EnvPrompt`
- [X] Consider using more compact Ids for fragments (eg. 16 hex characters)
- [X] Return an error if trying to create an already existing fragment? to force the LLM to use the update tool
- [ ] paginated children when reading a fragment, sorted by most recent?

### Remove persistence of system prompt as it should be calculated on the fly. It contains the current date and other stuff.
- [ ] delete the column from the database

### rework the registration of the `Repository` service and other services used by tools




## Other stuff:

### Workspace support
- [ ] allow the agent to access a folder and work within it.
- [ ] when workspace support update the `Environment.EnvPrompt`

### Multi-agent communication
- [ ] allow an agent to delegate a task to another agent
- [ ] async tasks with sharing of a memory fragment for communication

### Models Multiprovider support
- [ ] when doing this update the `Environment.EnvPrompt`

### Multi-model support
- [ ] allow the agent to change model to handle different tasks (eg. one default, one for coding, one for simpler tasks like summarizing, ecc...)

### LCM
- [ ] validate that tokens(summary) < tokens(history) and use aggressive directive if necessary

### LCM / Fragments integration
- [ ] TBC: during summarization remove fragments content and save only their Id's

### General improvements
- [ ] backup/export/restore functionality of database
- [ ] TBD: add delete of agents, older sessions, ecc... how should this work? do we delete the sessions or just mark them as deleted? what about agents?