## Most important stuff:

### Fragments improvements
- [ ] Add root fragments to `Environment.EnvPrompt`
- [ ] Consider using more compact Ids for fragments (eg. 16 hex characters)

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

### LCM
- [ ] validate that tokens(summary) < tokens(history) and use aggressive directive if necessary

### LCM / Fragments integration
- [ ] during summarization remove fragments content and save only their Id's