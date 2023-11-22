# Getting Access

## Azure Subscription

Options:
* Mail to @maggiexu and @ddazops to get DevDiv Azure individual subscription for the R&D purposes.
  * You'll receive subscription ID, that then needs to be specified when creating resources upon logging to Azure portal with your corp account
* Get the Azure credit through https://my.visualstudio.com/ - link it to your personal account.
* Create your own subscription

## OpenAI acccess

Upon logging to azure - find and click 'Azure OpenAI', then '+ Create' and you'll be brought to page which will require you to fill in a questionaire and wait for approval. Upon approval is granted you can return to the 'Create Azure OpenAI' dialog (don't forget to specify your subscription in here!)

## Crerating OpenAI connector and models

As per previous step. Don't forget to fill in proper subscription id. Chose 'West US' region.

Choose or create resource group to contain the services and choose name for your deployment. No need to fill any other information.

Once created - click your deployment and note endpoint and key information (under 'Endpoints' and/or 'Manage Keys' options).

Then choose 'Go to Azure OpenAI Studio' - there you'll be creating and managing your models.

New model can be created by 'Create New Deployment'.

For our purpose we will need:
 * Embeddings creator - e.g. text-embedding-ada-002
 * GPT model - any can be used.

 ## Running the sample app

Note the names of the created models, the api key and service endpoint (all per steps above) and store them in your environment as per prototypes\DotUtils.MSBuild.SandBox\Program.cs.

Run the embeddings generation once (`BinlogToEmbeddingsFile`), then you can repetitively run the query loop (`QueryBinlog`) providing the already generated and locally stored file.