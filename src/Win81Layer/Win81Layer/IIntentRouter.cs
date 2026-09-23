namespace Win81Layer;

public interface IIntentRouter
{
	Intent Classify(Query q);
}
